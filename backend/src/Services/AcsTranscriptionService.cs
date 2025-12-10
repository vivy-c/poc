using Azure;
using Azure.Communication.CallAutomation;
using CallTranscription.Functions.Common;
using CallTranscription.Functions.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CallTranscription.Functions.Services;

public class AcsTranscriptionService
{
    private readonly CallAutomationClient _callAutomationClient;
    private readonly CallSessionStore _callSessionStore;
    private readonly ILogger _logger;
    private readonly bool _speechConfigured;
    private readonly string _locale;
    private readonly string? _speechModelEndpointId;
    private readonly bool _sentimentEnabled;
    private readonly IList<string>? _languageIdLocales;
    private readonly PiiRedactionOptions? _piiOptions;
    private readonly SummarizationOptions? _summarizationOptions;
    private readonly Uri? _callbackUri;
    private readonly bool _transcriptionEnabled;

    public AcsTranscriptionService(
        CallSessionStore callSessionStore,
        IOptions<AcsOptions> acsOptions,
        IOptions<SpeechOptions> speechOptions,
        IOptions<WebhookAuthOptions> webhookOptions,
        IOptions<FeatureFlagsOptions> featureFlags,
        ILoggerFactory loggerFactory)
    {
        var connectionString = acsOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ACS connection string is missing (ACS__ConnectionString).");
        }

        _callAutomationClient = new CallAutomationClient(connectionString);
        _callSessionStore = callSessionStore;
        _logger = loggerFactory.CreateLogger<AcsTranscriptionService>();
        _transcriptionEnabled = featureFlags.Value.EnableTranscription;

        var speech = speechOptions.Value;
        _locale = string.IsNullOrWhiteSpace(speech.Locale) ? "en-US" : speech.Locale;
        _speechConfigured = !string.IsNullOrWhiteSpace(speech.Key)
            && (!string.IsNullOrWhiteSpace(speech.Region) || !string.IsNullOrWhiteSpace(speech.Endpoint));
        _logger.LogInformation("speech key: {SpeechKey}", speech.Key);
        _logger.LogInformation("speech region: {SpeechRegion}", speech.Region);
        _speechModelEndpointId = string.IsNullOrWhiteSpace(speech.SpeechRecognitionModelEndpointId)
            ? null
            : speech.SpeechRecognitionModelEndpointId;
        _sentimentEnabled = speech.EnableSentimentAnalysis;
        _piiOptions = BuildPiiOptions(speech);
        _languageIdLocales = ParseLocales(speech);
        _summarizationOptions = BuildSummarizationOptions(speech);
        if (!_speechConfigured)
        {
            _logger.LogWarning("Speech configuration missing; transcription orchestration will be a no-op.");
        }

        var webhook = webhookOptions.Value;
        if (!string.IsNullOrWhiteSpace(webhook.PublicBaseUrl))
        {
            var baseUrl = webhook.PublicBaseUrl.TrimEnd('/');
            if (Uri.TryCreate($"{baseUrl}/api/call-events", UriKind.Absolute, out var uri))
            {
                _callbackUri = uri;
            }
            else
            {
                _logger.LogWarning("Webhook public base URL is invalid; transcription callbacks will not be set.");
            }
        }
    }

    public async Task<bool> TryStartAsync(CallSession session, CancellationToken cancellationToken = default)
    {
        if (!_transcriptionEnabled)
        {
            _logger.LogInformation("Transcription disabled via feature flag; skipping start for {CallSessionId}", session.Id);
            return false;
        }

        if (!_speechConfigured)
        {
            return false;
        }

        if (session.TranscriptionStartedAtUtc is not null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(session.CallConnectionId))
        {
            _logger.LogWarning(
                "Cannot start transcription for {CallSessionId}: call connection id missing.",
                session.Id);
            return false;
        }

        try
        {
            var media = _callAutomationClient.GetCallConnection(session.CallConnectionId).GetCallMedia();
            var options = new StartTranscriptionOptions
            {
                Locale = "en-US",
                OperationContext = session.OperationContext,
                OperationCallbackUri = _callbackUri,
                SpeechRecognitionModelEndpointId = _speechModelEndpointId,
                PiiRedactionOptions = _piiOptions,
                IsSentimentAnalysisEnabled = _sentimentEnabled,
                SummarizationOptions = _summarizationOptions
            };

            // if (_languageIdLocales is { Count: > 0 })
            // {
            //     options.Locales = _languageIdLocales;
            // }

            await media.StartTranscriptionAsync(options, cancellationToken);
            await _callSessionStore.MarkTranscriptionStartedAsync(session.Id, cancellationToken);
            _logger.LogInformation(
                "Transcription started for {CallSessionId} (operationContext={OperationContext}, locale={Locale})",
                session.Id,
                session.OperationContext,
                _locale);
            return true;
        }
        catch (RequestFailedException ex)
        {
            if (IsAlreadyStarted(ex))
            {
                await _callSessionStore.MarkTranscriptionStartedAsync(session.Id, cancellationToken);
                _logger.LogInformation(
                    "Transcription already active for {CallSessionId}; marking as started (operationContext={OperationContext}, locale={Locale})",
                    session.Id,
                    session.OperationContext,
                    _locale);
                return true;
            }

            _logger.LogError(
                ex,
                "Failed to start transcription for call {CallSessionId} (connectionId={CallConnectionId}); status={Status}",
                session.Id,
                session.CallConnectionId,
                ex.Status);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to start transcription for call {CallSessionId} (connectionId={CallConnectionId})",
                session.Id,
                session.CallConnectionId);
            return false;
        }
    }

    public async Task<bool> TryStopAsync(CallSession session, CancellationToken cancellationToken = default)
    {
        if (!_transcriptionEnabled)
        {
            return false;
        }

        if (!_speechConfigured)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(session.CallConnectionId))
        {
            _logger.LogWarning(
                "Cannot stop transcription for {CallSessionId}: call connection id missing.",
                session.Id);
            return false;
        }

        try
        {
            var media = _callAutomationClient.GetCallConnection(session.CallConnectionId).GetCallMedia();
            var options = new StopTranscriptionOptions
            {
                OperationContext = session.OperationContext,
                OperationCallbackUri = _callbackUri?.ToString()
            };
            await media.StopTranscriptionAsync(options, cancellationToken);
            _logger.LogInformation(
                "Transcription stop requested for {CallSessionId} (operationContext={OperationContext})",
                session.Id,
                session.OperationContext);
            return true;
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(
                ex,
                "Failed to stop transcription for call {CallSessionId} (connectionId={CallConnectionId}); status={Status}",
                session.Id,
                session.CallConnectionId,
                ex.Status);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to stop transcription for call {CallSessionId} (connectionId={CallConnectionId})",
                session.Id,
                session.CallConnectionId);
            return false;
        }
    }

    private static PiiRedactionOptions? BuildPiiOptions(SpeechOptions speech)
    {
        if (!speech.EnablePiiRedaction)
        {
            return null;
        }

        var options = new PiiRedactionOptions
        {
            IsEnabled = true
        };

        if (!string.IsNullOrWhiteSpace(speech.PiiRedactionType))
        {
            options.RedactionType = speech.PiiRedactionType;
        }

        return options;
    }

    private static IList<string>? ParseLocales(SpeechOptions speech)
    {
        if (speech.Locales is { Count: > 0 })
        {
            return speech.Locales.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
        }

        if (string.IsNullOrWhiteSpace(speech.LocalesCsv))
        {
            return null;
        }

        return speech.LocalesCsv
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
    }

    private static SummarizationOptions? BuildSummarizationOptions(SpeechOptions speech)
    {
        if (!speech.EnableSummarization)
        {
            return null;
        }

        var options = new SummarizationOptions
        {
            IsEndCallSummaryEnabled = true,
            Locale = string.IsNullOrWhiteSpace(speech.SummarizationLocale)
                ? (string.IsNullOrWhiteSpace(speech.Locale) ? "en-US" : speech.Locale)
                : speech.SummarizationLocale
        };

        return options;
    }

    private static bool IsAlreadyStarted(RequestFailedException ex)
    {
        if (ex.ErrorCode is not null && ex.ErrorCode.Equals("8500", StringComparison.OrdinalIgnoreCase))
        {
            return ex.Message?.Contains("already started", StringComparison.OrdinalIgnoreCase) == true;
        }

        return ex.Message?.Contains("already started", StringComparison.OrdinalIgnoreCase) == true;
    }
}
