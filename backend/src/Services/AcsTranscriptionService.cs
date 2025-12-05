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

    public AcsTranscriptionService(
        CallSessionStore callSessionStore,
        IOptions<AcsOptions> acsOptions,
        IOptions<SpeechOptions> speechOptions,
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
        _speechConfigured = !string.IsNullOrWhiteSpace(speech.Key) && !string.IsNullOrWhiteSpace(speech.Region);
        if (!_speechConfigured)
        {
            _logger.LogWarning("Speech configuration missing; transcription orchestration will be a no-op.");
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
            // TODO: Replace placeholder once ACS exposes transcription start APIs in SDK.
            _callSessionStore.MarkTranscriptionStarted(session.Id);
            _logger.LogInformation(
                "Marked transcription as started for {CallSessionId} (operationContext={OperationContext})",
                session.Id,
                session.OperationContext);

            await Task.CompletedTask;
            return true;
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
            // TODO: Wire up ACS/Speech stop transcription call when available.
            _logger.LogInformation(
                "Transcription stop requested for {CallSessionId} (operationContext={OperationContext})",
                session.Id,
                session.OperationContext);

            await Task.CompletedTask;
            return true;
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
