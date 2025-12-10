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
    private readonly string _speechEndpoint;
    private readonly string _speechRegion;
    private readonly bool _speechConfigured;
    private readonly bool _startTranscriptionOnConnect;
    private readonly bool _enableIntermediateResults;
    private readonly string? _speechModelEndpointId;
    private readonly IList<string>? _languageIdLocales;
    private readonly bool _sentimentEnabled;
    private readonly PiiRedactionOptions? _piiOptions;
    private readonly SummarizationOptions? _summarizationOptions;
    private readonly Uri? _transportUriOverride;

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
        _speechEndpoint = speech.Endpoint ?? string.Empty;
        _speechRegion = speech.Region ?? string.Empty;
        _speechConfigured = !string.IsNullOrWhiteSpace(speech.Key)
            && (!string.IsNullOrWhiteSpace(_speechEndpoint) || !string.IsNullOrWhiteSpace(_speechRegion));
        _startTranscriptionOnConnect = speech.StartTranscriptionOnConnect;
        _enableIntermediateResults = speech.EnableIntermediateResults;
        _speechModelEndpointId = string.IsNullOrWhiteSpace(speech.SpeechRecognitionModelEndpointId)
            ? null
            : speech.SpeechRecognitionModelEndpointId;
        _languageIdLocales = ParseLocales(speech);
        _sentimentEnabled = speech.EnableSentimentAnalysis;
        _piiOptions = BuildPiiOptions(speech);
        _summarizationOptions = BuildSummarizationOptions(speech);
        _transportUriOverride = TryResolveCustomTransportUri(speech.TransportUri);

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
                var transportUrl = ResolveTransportUri();
                connectOptions.TranscriptionOptions = new TranscriptionOptions(_transcriptionLocale, StreamingTransport.Websocket)
                {
                    StartTranscription = _startTranscriptionOnConnect,
                    TransportUri = transportUrl,
                    EnableIntermediateResults = _enableIntermediateResults,
                    SpeechRecognitionModelEndpointId = _speechModelEndpointId,
                    IsSentimentAnalysisEnabled = _sentimentEnabled,
                    PiiRedactionOptions = _piiOptions,
                    SummarizationOptions = _summarizationOptions
                };

                if (_languageIdLocales is { Count: > 0 })
                {
                    connectOptions.TranscriptionOptions.Locales = _languageIdLocales;
                }

                var cognitiveServicesEndpoint = ResolveCognitiveServicesEndpoint();
                if (cognitiveServicesEndpoint is not null)
                {
                    if (string.IsNullOrWhiteSpace(_speechEndpoint))
                    {
                        _logger.LogWarning(
                            "Speech__Endpoint not set; falling back to region-based endpoint {Endpoint}. Prefer setting Speech__Endpoint to your Speech resource endpoint (e.g. https://<resource>.cognitiveservices.azure.com) to avoid Cognitive Services bad request errors.",
                            cognitiveServicesEndpoint);
                    }

                    connectOptions.CallIntelligenceOptions = new CallIntelligenceOptions
                    {
                        CognitiveServicesEndpoint = cognitiveServicesEndpoint
                    };
                }
                else
                {
                    _logger.LogWarning(
                        "Speech endpoint not configured; skipping CallIntelligenceOptions. Transcription will fail until Speech__Endpoint (preferred) or Speech__Region is set to a valid Speech resource endpoint.");
                }
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

    private Uri ResolveTransportUri()
    {
        if (_transportUriOverride is not null)
        {
            return _transportUriOverride;
        }

        var defaultUrl =
            $"wss://{_speechRegion}.stt.speech.microsoft.com/speech/recognition/conversation/cognitiveservices/v1?language={_transcriptionLocale}";
        return new Uri(defaultUrl);
    }

    private Uri? ResolveCognitiveServicesEndpoint()
    {
        if (!string.IsNullOrWhiteSpace(_speechEndpoint)
            && Uri.TryCreate(_speechEndpoint.Trim(), UriKind.Absolute, out var explicitEndpoint))
        {
            return explicitEndpoint;
        }

        if (!string.IsNullOrWhiteSpace(_speechRegion))
        {
            var fallback = $"https://{_speechRegion}.api.cognitive.microsoft.com";
            if (Uri.TryCreate(fallback, UriKind.Absolute, out var fallbackUri))
            {
                return fallbackUri;
            }
        }

        return null;
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

    private static Uri? TryResolveCustomTransportUri(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
        {
            return uri;
        }

        return null;
    }
}
