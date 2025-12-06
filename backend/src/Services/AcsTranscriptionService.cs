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
    private readonly Uri? _callbackUri;

    public AcsTranscriptionService(
        CallSessionStore callSessionStore,
        IOptions<AcsOptions> acsOptions,
        IOptions<SpeechOptions> speechOptions,
        IOptions<WebhookAuthOptions> webhookOptions,
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

        var speech = speechOptions.Value;
        _locale = string.IsNullOrWhiteSpace(speech.Locale) ? "en-US" : speech.Locale;
        _speechConfigured = !string.IsNullOrWhiteSpace(speech.Key) && !string.IsNullOrWhiteSpace(speech.Region);
        if (!_speechConfigured)
        {
            _logger.LogWarning("Speech configuration missing; transcription orchestration will be a no-op.");
        }

        var webhook = webhookOptions.Value;
        if (!string.IsNullOrWhiteSpace(webhook.PublicBaseUrl))
        {
            var baseUrl = webhook.PublicBaseUrl.TrimEnd('/');
            if (Uri.TryCreate($"{baseUrl}/api/acs/events", UriKind.Absolute, out var uri))
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
                Locale = _locale,
                OperationContext = session.OperationContext,
                OperationCallbackUri = _callbackUri
            };

            await media.StartTranscriptionAsync(options, cancellationToken);
            _callSessionStore.MarkTranscriptionStarted(session.Id);
            _logger.LogInformation(
                "Transcription started for {CallSessionId} (operationContext={OperationContext}, locale={Locale})",
                session.Id,
                session.OperationContext,
                _locale);
            return true;
        }
        catch (RequestFailedException ex)
        {
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
}
