using Azure.Communication;
using Azure.Communication.CallAutomation;
using CallTranscription.Functions.Common;
using CallTranscription.Functions.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CallParticipantModel = CallTranscription.Functions.Models.CallParticipant;

namespace CallTranscription.Functions.Services;

public class AcsCallService
{
    private readonly CallAutomationClient _callAutomationClient;
    private readonly ILogger _logger;
    private readonly Uri? _callbackUri;
    private readonly string _transcriptionLocale;
    private readonly bool _transcriptionEnabled;
    private readonly string _speechRegion;
    private readonly bool _speechConfigured;

    public AcsCallService(
        IOptions<AcsOptions> options,
        IOptions<WebhookAuthOptions> webhookOptions,
        IOptions<SpeechOptions> speechOptions,
        IOptions<FeatureFlagsOptions> featureFlags,
        ILoggerFactory loggerFactory)
    {
        var connectionString = options.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ACS connection string is missing (ACS__ConnectionString).");
        }

        _callAutomationClient = new CallAutomationClient(connectionString);
        _logger = loggerFactory.CreateLogger<AcsCallService>();

        var speech = speechOptions.Value;
        _transcriptionLocale = string.IsNullOrWhiteSpace(speech.Locale) ? "en-US" : speech.Locale;
        _transcriptionEnabled = featureFlags.Value.EnableTranscription;
        _speechRegion = speech.Region ?? string.Empty;
        _speechConfigured = !string.IsNullOrWhiteSpace(speech.Key) && !string.IsNullOrWhiteSpace(_speechRegion);

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
                _logger.LogWarning("Webhook public base URL is invalid; ACS call callbacks will not be set.");
            }
        }
        else
        {
            _logger.LogWarning("Webhook public base URL missing; ACS call callbacks will not be set.");
        }
    }

    public async Task<bool> TryAddParticipantAsync(
        Guid callSessionId,
        string? callConnectionId,
        CallParticipantModel participant,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(callConnectionId))
        {
            _logger.LogWarning(
                "Call connection id missing; skipping ACS participant add for {ParticipantId} (callSession={CallSessionId})",
                participant.DemoUserId,
                callSessionId);
            return false;
        }

        try
        {
            var callConnection = _callAutomationClient.GetCallConnection(callConnectionId);
            var addOptions = new AddParticipantOptions(new CallInvite(new CommunicationUserIdentifier(participant.AcsIdentity)))
            {
                OperationContext = callSessionId.ToString()
            };

            await callConnection.AddParticipantAsync(addOptions, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "ACS add participant failed for {ParticipantId} on call {CallConnectionId}",
                participant.DemoUserId,
                callConnectionId);
            return false;
        }
    }

    public async Task<(bool Success, string? CallConnectionId, string? ServerCallId)> TryEnsureCallConnectionAsync(
        Guid callSessionId,
        string acsGroupId,
        CancellationToken cancellationToken = default)
    {
        if (_callbackUri is null)
        {
            _logger.LogWarning(
                "Cannot connect server-side to call {CallSessionId}: callback URI not configured (Webhook__PublicBaseUrl).",
                callSessionId);
            return (false, null, null);
        }

        if (!Guid.TryParse(acsGroupId, out var groupId))
        {
            _logger.LogWarning(
                "Cannot connect server-side to call {CallSessionId}: invalid ACS group id {AcsGroupId}.",
                callSessionId,
                acsGroupId);
            return (false, null, null);
        }

        try
        {
            var connectOptions = new ConnectCallOptions(new GroupCallLocator(groupId.ToString()), _callbackUri)
            {
                OperationContext = callSessionId.ToString()
            };

            if (_transcriptionEnabled && _speechConfigured)
            {
                var transportUrl = new Uri($"wss://{_speechRegion}.stt.speech.microsoft.com/speech/recognition/conversation/cognitiveservices/v1?language={_transcriptionLocale}");
                connectOptions.TranscriptionOptions = new TranscriptionOptions(_transcriptionLocale, StreamingTransport.Websocket)
                {
                    StartTranscription = false,
                    TransportUri = transportUrl
                };

                var cognitiveServicesEndpoint = new Uri($"https://{_speechRegion}.api.cognitive.microsoft.com");
                connectOptions.CallIntelligenceOptions = new CallIntelligenceOptions
                {
                    CognitiveServicesEndpoint = cognitiveServicesEndpoint
                };
            }

            var response = await _callAutomationClient.ConnectCallAsync(connectOptions, cancellationToken);
            var properties = response?.Value?.CallConnectionProperties;
            if (properties is null)
            {
                _logger.LogWarning(
                    "ConnectCallAsync returned no properties for call {CallSessionId} (group {AcsGroupId})",
                    callSessionId,
                    acsGroupId);
                return (false, null, null);
            }

            _logger.LogInformation(
                "Server-side call connected for {CallSessionId} (callConnectionId={CallConnectionId}, serverCallId={ServerCallId})",
                callSessionId,
                properties.CallConnectionId,
                properties.ServerCallId);

            return (true, properties.CallConnectionId, properties.ServerCallId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to connect server-side to call {CallSessionId} (group {AcsGroupId})",
                callSessionId,
                acsGroupId);
            return (false, null, null);
        }
    }
}
