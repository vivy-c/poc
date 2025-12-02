using System.Net;
using System.Text.Json;
using CallTranscription.Functions.Common;
using CallTranscription.Functions.Models;
using CallTranscription.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CallTranscription.Functions.Functions;

public class AcsEventsFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CallSessionStore _callSessionStore;
    private readonly TranscriptStore _transcriptStore;
    private readonly ResponseFactory _responseFactory;
    private readonly ILogger _logger;

    public AcsEventsFunction(
        CallSessionStore callSessionStore,
        TranscriptStore transcriptStore,
        ResponseFactory responseFactory,
        ILoggerFactory loggerFactory)
    {
        _callSessionStore = callSessionStore;
        _transcriptStore = transcriptStore;
        _responseFactory = responseFactory;
        _logger = loggerFactory.CreateLogger<AcsEventsFunction>();
    }

    [Function("acs-events")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", "options", Route = "acs/events")] HttpRequestData req)
    {
        if (string.Equals(req.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            var preflight = req.CreateResponse(HttpStatusCode.NoContent);
            preflight.Headers.Add("Access-Control-Allow-Origin", "*");
            preflight.Headers.Add("Access-Control-Allow-Methods", "POST,OPTIONS");
            preflight.Headers.Add("Access-Control-Allow-Headers", "content-type");
            return preflight;
        }

        List<IncomingEvent> events;
        try
        {
            using var document = await JsonDocument.ParseAsync(req.Body);
            events = ParseEvents(document.RootElement).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse ACS events payload");
            var bad = _responseFactory.CreateJson(req, HttpStatusCode.BadRequest, new { error = "Invalid events payload." });
            bad.Headers.Add("Access-Control-Allow-Origin", "*");
            return bad;
        }

        if (events.Count == 0)
        {
            var bad = _responseFactory.CreateJson(req, HttpStatusCode.BadRequest, new { error = "No events found." });
            bad.Headers.Add("Access-Control-Allow-Origin", "*");
            return bad;
        }

        foreach (var evt in events)
        {
            if (HandleSubscriptionValidation(req, evt))
            {
                var validation = _responseFactory.CreateJson(req, HttpStatusCode.OK, new { validationResponse = evt.Data?.ValidationCode });
                validation.Headers.Add("Access-Control-Allow-Origin", "*");
                return validation;
            }

            await DispatchEventAsync(evt);
        }

        var ok = _responseFactory.CreateJson(
            req,
            HttpStatusCode.Accepted,
            new
            {
                received = events.Count
            });
        ok.Headers.Add("Access-Control-Allow-Origin", "*");
        return ok;
    }

    private async Task DispatchEventAsync(IncomingEvent evt)
    {
        var type = evt.EventType?.ToLowerInvariant() ?? string.Empty;
        var data = evt.Data;
        var callSessionId = ResolveCallSessionId(data);

        if (callSessionId is null)
        {
            _logger.LogWarning(
                "Received ACS event {EventType} but could not map to a call session (groupId={AcsGroupId}, callConnection={CallConnectionId})",
                evt.EventType,
                data?.AcsGroupId,
                data?.CallConnectionId ?? data?.ServerCallId);
            return;
        }

        if (type.Contains("callconnected"))
        {
            if (!string.IsNullOrWhiteSpace(data?.CallConnectionId))
            {
                _callSessionStore.SetCallConnection(callSessionId.Value, data.CallConnectionId);
            }

            _callSessionStore.UpdateStatus(callSessionId.Value, "Connected");
            _callSessionStore.MarkTranscriptionStarted(callSessionId.Value);

            _logger.LogInformation(
                "Call {CallSessionId} connected; transcription pipeline started",
                callSessionId);
            return;
        }

        if (type.Contains("callstarted") || type.Contains("callconnecting"))
        {
            _callSessionStore.UpdateStatus(callSessionId.Value, "Connecting");
            if (!string.IsNullOrWhiteSpace(data?.CallConnectionId))
            {
                _callSessionStore.SetCallConnection(callSessionId.Value, data.CallConnectionId);
            }
            return;
        }

        if (type.Contains("callended") || type.Contains("calldisconnected"))
        {
            _callSessionStore.UpdateStatus(callSessionId.Value, "Completed", DateTime.UtcNow);
            _logger.LogInformation("Call {CallSessionId} marked completed from ACS event", callSessionId);
            return;
        }

        if (type.Contains("transcript") || type.Contains("transcription"))
        {
            var segments = BuildSegments(callSessionId.Value, data).ToList();
            if (segments.Count == 0)
            {
                _logger.LogDebug("Transcript event contained no text for {CallSessionId}", callSessionId);
                return;
            }

            _transcriptStore.Add(callSessionId.Value, segments);
            _logger.LogInformation(
                "Persisted {SegmentCount} transcript segment(s) for call {CallSessionId}",
                segments.Count,
                callSessionId);
            return;
        }

        await Task.CompletedTask;
    }

    private Guid? ResolveCallSessionId(AcsEventData? data)
    {
        if (data is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(data.CallSessionId) && Guid.TryParse(data.CallSessionId, out var id))
        {
            return id;
        }

        if (!string.IsNullOrWhiteSpace(data.OperationContext) && Guid.TryParse(data.OperationContext, out var fromContext))
        {
            return fromContext;
        }

        if (!string.IsNullOrWhiteSpace(data.AcsGroupId))
        {
            var byGroup = _callSessionStore.FindByAcsGroupId(data.AcsGroupId);
            if (byGroup is not null)
            {
                return byGroup.Id;
            }
        }

        if (!string.IsNullOrWhiteSpace(data.CallConnectionId))
        {
            var byConnection = _callSessionStore.FindByCallConnectionId(data.CallConnectionId);
            if (byConnection is not null)
            {
                return byConnection.Id;
            }
        }

        if (!string.IsNullOrWhiteSpace(data.ServerCallId))
        {
            var byServerId = _callSessionStore.FindByCallConnectionId(data.ServerCallId);
            if (byServerId is not null)
            {
                return byServerId.Id;
            }
        }

        return null;
    }

    private IEnumerable<TranscriptSegment> BuildSegments(Guid callSessionId, AcsEventData? data)
    {
        if (data is null)
        {
            yield break;
        }

        if (data.Segments is not null)
        {
            foreach (var segment in data.Segments)
            {
                var text = segment.Text ?? data.Text;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                yield return CreateSegment(callSessionId, segment, data, text);
            }

            yield break;
        }

        var payload = data.Transcription;
        var bodyText = payload?.Text ?? data.Text;
        if (string.IsNullOrWhiteSpace(bodyText))
        {
            yield break;
        }

        yield return CreateSegment(callSessionId, payload, data, bodyText);
    }

    private TranscriptSegment CreateSegment(Guid callSessionId, AcsTranscriptionData? source, AcsEventData fallback, string text)
    {
        var offset = source?.OffsetSeconds ?? source?.Offset ?? fallback.OffsetSeconds ?? fallback.Offset;
        var duration = source?.DurationSeconds ?? source?.Duration ?? fallback.DurationSeconds ?? fallback.Duration;

        return new TranscriptSegment(
            Guid.NewGuid(),
            callSessionId,
            text.Trim(),
            source?.SpeakerId ?? fallback.SpeakerId ?? fallback.ParticipantId,
            source?.SpeakerDisplayName ?? fallback.SpeakerDisplayName ?? fallback.ParticipantDisplayName,
            offset,
            duration,
            DateTimeOffset.UtcNow,
            Source: fallback.Locale ?? source?.Locale ?? "acs");
    }

    private static IEnumerable<IncomingEvent> ParseEvents(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in root.EnumerateArray())
            {
                if (TryParseEvent(element, out var evt))
                {
                    yield return evt;
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Object && TryParseEvent(root, out var single))
        {
            yield return single;
        }
    }

    private static bool TryParseEvent(JsonElement element, out IncomingEvent evt)
    {
        evt = default!;

        if (!TryGetString(element, "eventType", out var eventType) && !TryGetString(element, "type", out eventType))
        {
            return false;
        }

        AcsEventData? data = null;
        if (TryGetProperty(element, "data", out var dataElement))
        {
            data = dataElement.Deserialize<AcsEventData>(JsonOptions);
        }

        DateTimeOffset? eventTime = null;
        if (TryGetProperty(element, "eventTime", out var timeElement)
            && timeElement.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(timeElement.GetString(), out var parsedTime))
        {
            eventTime = parsedTime;
        }

        evt = new IncomingEvent(eventType!, data, eventTime);
        return true;
    }

    private bool HandleSubscriptionValidation(HttpRequestData req, IncomingEvent evt)
    {
        if (!string.Equals(evt.EventType, "Microsoft.EventGrid.SubscriptionValidationEvent", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (evt.Data?.ValidationCode is null)
        {
            return false;
        }

        _logger.LogInformation("EventGrid subscription validation requested");
        return true;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (TryGetProperty(element, propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return true;
        }

        return false;
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

    private sealed record IncomingEvent(string EventType, AcsEventData? Data, DateTimeOffset? EventTime);

    private sealed class AcsEventData
    {
        public string? CallSessionId { get; set; }
        public string? AcsGroupId { get; set; }
        public string? GroupCallId { get; set; }
        public string? CallConnectionId { get; set; }
        public string? ServerCallId { get; set; }
        public string? OperationContext { get; set; }
        public string? Status { get; set; }
        public string? Reason { get; set; }
        public string? ParticipantId { get; set; }
        public string? ParticipantDisplayName { get; set; }
        public string? SpeakerId { get; set; }
        public string? SpeakerDisplayName { get; set; }
        public string? Locale { get; set; }
        public string? Text { get; set; }
        public double? Offset { get; set; }
        public double? OffsetSeconds { get; set; }
        public double? Duration { get; set; }
        public double? DurationSeconds { get; set; }
        public bool? IsFinal { get; set; }
        public string? ValidationCode { get; set; }
        public IEnumerable<AcsTranscriptionData>? Segments { get; set; }
        public AcsTranscriptionData? Transcription { get; set; }
    }

    private sealed class AcsTranscriptionData
    {
        public string? Text { get; set; }
        public string? SpeakerId { get; set; }
        public string? SpeakerDisplayName { get; set; }
        public double? Offset { get; set; }
        public double? OffsetSeconds { get; set; }
        public double? Duration { get; set; }
        public double? DurationSeconds { get; set; }
        public bool? IsFinal { get; set; }
        public string? Locale { get; set; }
    }
}
