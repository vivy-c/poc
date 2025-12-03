using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using CallTranscription.Functions.Common;
using CallTranscription.Functions.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CallTranscription.Functions.Services;

public class CallSummaryService
{
    private readonly CallSessionStore _callSessionStore;
    private readonly TranscriptStore _transcriptStore;
    private readonly CallSummaryStore _summaryStore;
    private readonly OpenAIClient? _openAiClient;
    private readonly OpenAiOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, Task<CallSummary?>> _inFlight = new();

    public CallSummaryService(
        CallSessionStore callSessionStore,
        TranscriptStore transcriptStore,
        CallSummaryStore summaryStore,
        IOptions<OpenAiOptions> options,
        ILoggerFactory loggerFactory)
    {
        _callSessionStore = callSessionStore;
        _transcriptStore = transcriptStore;
        _summaryStore = summaryStore;
        _options = options.Value;
        _logger = loggerFactory.CreateLogger<CallSummaryService>();

        if (!string.IsNullOrWhiteSpace(_options.Endpoint) && !string.IsNullOrWhiteSpace(_options.Key))
        {
            try
            {
                _openAiClient = new OpenAIClient(new Uri(_options.Endpoint), new AzureKeyCredential(_options.Key));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create OpenAI client; summaries will use fallback output.");
            }
        }
        else
        {
            _logger.LogWarning("OpenAI configuration missing; summaries will use deterministic fallback text.");
        }
    }

    public Task<CallSummary?> EnsureSummaryAsync(Guid callSessionId, CancellationToken cancellationToken = default)
    {
        var existing = _summaryStore.GetByCallSession(callSessionId);
        if (existing is not null)
        {
            return Task.FromResult<CallSummary?>(existing);
        }

        return _inFlight.GetOrAdd(
            callSessionId,
            _ => GenerateAndPersistAsync(callSessionId, cancellationToken));
    }

    private async Task<CallSummary?> GenerateAndPersistAsync(Guid callSessionId, CancellationToken cancellationToken)
    {
        try
        {
            var summary = await GenerateSummaryInternalAsync(callSessionId, cancellationToken);
            if (summary is null)
            {
                return null;
            }

            _summaryStore.Save(summary);
            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate summary for call {CallSessionId}", callSessionId);
            return null;
        }
        finally
        {
            _inFlight.TryRemove(callSessionId, out _);
        }
    }

    private async Task<CallSummary?> GenerateSummaryInternalAsync(Guid callSessionId, CancellationToken cancellationToken)
    {
        var session = _callSessionStore.Get(callSessionId);
        if (session is null)
        {
            _logger.LogWarning("Cannot summarize unknown call {CallSessionId}", callSessionId);
            return null;
        }

        var segments = _transcriptStore.GetByCallSession(callSessionId);
        CallSummary? summary = null;

        if (_openAiClient is not null && !string.IsNullOrWhiteSpace(_options.DeploymentName))
        {
            summary = await TrySummarizeWithOpenAiAsync(session, segments, cancellationToken);
        }

        summary ??= BuildFallbackSummary(session, segments);
        return summary;
    }

    private async Task<CallSummary?> TrySummarizeWithOpenAiAsync(
        CallSession session,
        IReadOnlyList<TranscriptSegment> segments,
        CancellationToken cancellationToken)
    {
        try
        {
            var systemPrompt =
                "You summarize recruiting calls. Return compact JSON with: " +
                "{ \"summary\": string, \"keyPoints\": [string], \"actionItems\": [string] }. " +
                "Summary <= 120 words; 3-6 keyPoints; actionItems must be concrete next steps.";

            var options = new ChatCompletionsOptions
            {
                Temperature = 0.35f,
                MaxTokens = 700
            };

            options.DeploymentName = _options.DeploymentName;

            options.Messages.Add(new ChatRequestSystemMessage(systemPrompt));
            options.Messages.Add(new ChatRequestUserMessage(BuildPrompt(session, segments)));

            var response = await _openAiClient!.GetChatCompletionsAsync(
                options,
                cancellationToken);

            var content = response.Value?.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning("OpenAI returned empty content for call {CallSessionId}", session.Id);
                return null;
            }

            var parsed = ParseSummaryJson(content);
            if (parsed is null)
            {
                _logger.LogWarning("OpenAI summary response could not be parsed for call {CallSessionId}", session.Id);
                return null;
            }

            return new CallSummary(
                Guid.NewGuid(),
                session.Id,
                parsed.Value.Summary,
                parsed.Value.KeyPoints,
                parsed.Value.ActionItems,
                DateTimeOffset.UtcNow,
                Source: "openai");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "OpenAI summary generation failed for call {CallSessionId}",
                session.Id);
            return null;
        }
    }

    private CallSummary BuildFallbackSummary(CallSession session, IReadOnlyList<TranscriptSegment> segments)
    {
        var participants = session.Participants?.Select(p => p.DisplayName).ToList() ?? new List<string>();
        if (participants.Count == 0)
        {
            participants.Add("unknown participants");
        }

        var summaryText = segments.Count == 0
            ? $"Call between {string.Join(", ", participants)} has no transcript yet. Summary pending."
            : $"Call between {string.Join(", ", participants)} captured {segments.Count} transcript segment(s). AI summary unavailable; showing a quick digest from the transcript.";

        var keyPoints = segments.Take(3)
            .Select(s => $"{s.SpeakerDisplayName ?? s.SpeakerDemoUserId ?? "Speaker"}: {Truncate(s.Text, 140)}")
            .ToList();

        if (keyPoints.Count == 0)
        {
            keyPoints.Add("Transcript not yet available for this call.");
        }

        var actionItems = new List<string>
        {
            "Model-generated action items unavailable. Review transcript and capture next steps."
        };

        return new CallSummary(
            Guid.NewGuid(),
            session.Id,
            summaryText,
            keyPoints,
            actionItems,
            DateTimeOffset.UtcNow,
            Source: "fallback");
    }

    private static string BuildPrompt(CallSession session, IReadOnlyList<TranscriptSegment> segments)
    {
        var initiator = session.Participants.FirstOrDefault(
            p => string.Equals(p.DemoUserId, session.StartedByDemoUserId, StringComparison.OrdinalIgnoreCase));
        var initiatorName = initiator?.DisplayName ?? session.StartedByDemoUserId;

        var sb = new StringBuilder();
        sb.AppendLine("Summarize this recruiting call.");
        sb.AppendLine($"Started at (UTC): {session.StartedAtUtc:u}");
        if (session.EndedAtUtc is not null)
        {
            sb.AppendLine($"Ended at (UTC): {session.EndedAtUtc:u}");
        }
        sb.AppendLine($"Started by: {initiatorName}");
        sb.AppendLine("Participants:");
        foreach (var participant in session.Participants)
        {
            sb.AppendLine($"- {participant.DisplayName} ({participant.DemoUserId})");
        }

        sb.AppendLine();
        sb.AppendLine("Transcript:");

        if (segments.Count == 0)
        {
            sb.AppendLine("No transcript captured.");
        }
        else
        {
            foreach (var segment in segments)
            {
                var speaker = segment.SpeakerDisplayName
                    ?? segment.SpeakerDemoUserId
                    ?? "Speaker";
                sb.AppendLine($"{speaker}: {segment.Text}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Return JSON only with fields: summary, keyPoints, actionItems.");

        return sb.ToString();
    }

    private static (string Summary, IReadOnlyList<string> KeyPoints, IReadOnlyList<string> ActionItems)? ParseSummaryJson(string content)
    {
        var json = ExtractJson(content);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            string summary = TryGetString(root, "summary")
                ?? TryGetString(root, "overview")
                ?? string.Empty;

            var keyPoints = ReadStringList(root, "keyPoints") ?? ReadStringList(root, "key_points") ?? new List<string>();
            var actionItems = ReadStringList(root, "actionItems") ?? ReadStringList(root, "action_items") ?? new List<string>();

            if (string.IsNullOrWhiteSpace(summary) && keyPoints.Count == 0 && actionItems.Count == 0)
            {
                return null;
            }

            return (summary.Trim(), keyPoints, actionItems);
        }
        catch
        {
            return null;
        }
    }

    private static List<string>? ReadStringList(JsonElement root, string propertyName)
    {
        if (!TryGetProperty(root, propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            var value = property.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : new List<string> { value };
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>();
        foreach (var element in property.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    list.Add(value.Trim());
                }
            }
        }

        return list;
    }

    private static string? ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            return trimmed[firstBrace..(lastBrace + 1)];
        }

        return trimmed;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (TryGetProperty(element, propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        return null;
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        return text.Substring(0, maxLength).TrimEnd() + "...";
    }
}
