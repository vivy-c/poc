using System.Net;
using System.Text.Json;
using CallTranscription.Functions.Common;
using CallTranscription.Functions.Models;
using CallTranscription.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CallTranscription.Functions.Functions;

public class AcsEventsFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CallSessionStore _callSessionStore;
    private readonly TranscriptStore _transcriptStore;
    private readonly CallSummaryService _callSummaryService;
    private readonly AcsTranscriptionService _transcriptionService;
    private readonly ResponseFactory _responseFactory;
    private readonly WebhookAuthOptions _webhookOptions;
    private readonly ILogger _logger;

    public AcsEventsFunction(
        CallSessionStore callSessionStore,
        TranscriptStore transcriptStore,
        CallSummaryService callSummaryService,
        AcsTranscriptionService transcriptionService,
        ResponseFactory responseFactory,
        IOptions<WebhookAuthOptions> webhookOptions,
        ILoggerFactory loggerFactory)
    {
        _callSessionStore = callSessionStore;
        _transcriptStore = transcriptStore;
        _callSummaryService = callSummaryService;
        _transcriptionService = transcriptionService;
        _responseFactory = responseFactory;
        _webhookOptions = webhookOptions.Value;
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
            preflight.Headers.Add("Access-Control-Allow-Headers", BuildAllowedHeaders());
            return preflight;
        }

        if (!IsAuthorized(req))
        {
            var unauthorized = _responseFactory.CreateJson(
                req,
                HttpStatusCode.Unauthorized,
                new { error = "Unauthorized ACS webhook call." });
            unauthorized.Headers.Add("Access-Control-Allow-Origin", "*");
            unauthorized.Headers.Add("Access-Control-Allow-Headers", BuildAllowedHeaders());
            return unauthorized;
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

        if (TryCreateSubscriptionValidationResponse(req, events, out var validationResponse))
        {
            return validationResponse;
        }

        foreach (var evt in events)
        {
            await DispatchEventAsync(evt, req.FunctionContext.CancellationToken);
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

    private async Task DispatchEventAsync(IncomingEvent evt, CancellationToken cancellationToken)
    {
        var type = evt.EventType?.ToLowerInvariant() ?? string.Empty;
        var data = evt.Data;
        var callSessionId = ResolveCallSessionId(data);
        var callSession = callSessionId is null ? null : _callSessionStore.Get(callSessionId.Value);

        _logger.LogInformation(
            "ACS event {EventType} (groupId={AcsGroupId}, callConnectionId={CallConnectionId}, serverCallId={ServerCallId}, operationContext={OperationContext}, resolvedCallSessionId={ResolvedCallSessionId})",
            evt.EventType,
            data?.AcsGroupId ?? data?.GroupCallId,
            data?.CallConnectionId,
            data?.ServerCallId,
            data?.OperationContext,
            callSessionId);

        if (callSessionId is null)
        {
            _logger.LogWarning(
                "Received ACS event {EventType} but could not map to a call session (groupId={AcsGroupId}, callConnection={CallConnectionId}, serverCallId={ServerCallId}, serverCallIdDecoded={ServerCallIdDecoded}, operationContext={OperationContext}, callSessionIdField={CallSessionIdField}, groupCallId={GroupCallId})",
                evt.EventType,
                data?.AcsGroupId ?? data?.GroupCallId,
                data?.CallConnectionId,
                data?.ServerCallId,
                DecodeBase64Safe(data?.ServerCallId),
                data?.OperationContext,
                data?.CallSessionId,
                data?.GroupCallId);

            if (!string.IsNullOrWhiteSpace(data?.ServerCallId))
            {
                var pending = _callSessionStore.FindPendingConnection();
                if (pending is not null)
                {
                    var resolvedId = pending.Id;
                    _callSessionStore.SetCallConnection(resolvedId, data.ServerCallId);
                    _logger.LogInformation(
                        "Mapped event {EventType} to pending session {CallSessionId} via serverCallId fallback",
                        evt.EventType,
                        resolvedId);
                    data.CallSessionId = resolvedId.ToString();
                    callSessionId = resolvedId;
                    callSession = pending;
                }
            }
            if (callSessionId is null)
            {
                return;
            }
        }

        if (type.Contains("callconnected"))
        {
            var connectionId = data?.CallConnectionId ?? data?.ServerCallId;
            if (!string.IsNullOrWhiteSpace(connectionId))
            {
                _callSessionStore.SetCallConnection(callSessionId.Value, connectionId);
            }

            _callSessionStore.UpdateStatus(callSessionId.Value, "Connected");

            var latestSession = _callSessionStore.Get(callSessionId.Value);
            if (latestSession is not null)
            {
                await _transcriptionService.TryStartAsync(latestSession, cancellationToken);
            }

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
            var latestSession = _callSessionStore.Get(callSessionId.Value);
            if (latestSession is not null)
            {
                _ = _transcriptionService.TryStopAsync(latestSession, CancellationToken.None);
            }
            _ = _callSummaryService.EnsureSummaryAsync(callSessionId.Value, CancellationToken.None);
            return;
        }

        if (type.Contains("transcript") || type.Contains("transcription"))
        {
            if (callSession is not null)
            {
                _callSessionStore.MarkTranscriptionStarted(callSession.Id);
            }

            var segments = BuildSegments(callSessionId.Value, callSession, data).ToList();
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

        if (!string.IsNullOrWhiteSpace(data.GroupCallId))
        {
            var byGroupId = _callSessionStore.FindByAcsGroupId(data.GroupCallId);
            if (byGroupId is not null)
            {
                return byGroupId.Id;
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
            var decoded = DecodeBase64Safe(data.ServerCallId);
            var byServerId = _callSessionStore.FindByCallConnectionId(data.ServerCallId)
                ?? (!string.IsNullOrWhiteSpace(decoded)
                    ? _callSessionStore.FindByCallConnectionId(decoded)
                    : null);
            if (byServerId is not null)
            {
                return byServerId.Id;
            }
        }

        // Last resort: map to the first pending active/connecting session (POC fallback).
        var pending = _callSessionStore.FindPendingConnection();
        return pending?.Id;
    }

    private IEnumerable<TranscriptSegment> BuildSegments(Guid callSessionId, CallSession? callSession, AcsEventData? data)
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

                yield return CreateSegment(callSessionId, callSession, segment, data, text);
            }

            yield break;
        }

        var payload = data.Transcription;
        var bodyText = payload?.Text ?? data.Text;
        if (string.IsNullOrWhiteSpace(bodyText))
        {
            yield break;
        }

        yield return CreateSegment(callSessionId, callSession, payload, data, bodyText);
    }

    private TranscriptSegment CreateSegment(Guid callSessionId, CallSession? callSession, AcsTranscriptionData? source, AcsEventData fallback, string text)
    {
        var offset = source?.OffsetSeconds ?? source?.Offset ?? fallback.OffsetSeconds ?? fallback.Offset;
        var duration = source?.DurationSeconds ?? source?.Duration ?? fallback.DurationSeconds ?? fallback.Duration;
        var speakerAcsId = source?.SpeakerId ?? fallback.SpeakerId ?? fallback.ParticipantId;
        var speakerName = source?.SpeakerDisplayName ?? fallback.SpeakerDisplayName ?? fallback.ParticipantDisplayName;

        string? speakerDemoUserId = null;
        string? resolvedDisplayName = speakerName;

        if (callSession?.Participants is not null && !string.IsNullOrWhiteSpace(speakerAcsId))
        {
            var byAcs = callSession.Participants.FirstOrDefault(
                p => string.Equals(p.AcsIdentity, speakerAcsId, StringComparison.OrdinalIgnoreCase));
            if (byAcs is not null)
            {
                speakerDemoUserId = byAcs.DemoUserId;
                resolvedDisplayName = byAcs.DisplayName;
            }
        }

        if (speakerDemoUserId is null && callSession?.Participants is not null && !string.IsNullOrWhiteSpace(speakerName))
        {
            var byName = callSession.Participants.FirstOrDefault(
                p => string.Equals(p.DisplayName, speakerName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                speakerDemoUserId = byName.DemoUserId;
                resolvedDisplayName = byName.DisplayName;
                speakerAcsId ??= byName.AcsIdentity;
            }
        }

        return new TranscriptSegment(
            Guid.NewGuid(),
            callSessionId,
            text.Trim(),
            speakerAcsId,
            speakerDemoUserId,
            resolvedDisplayName,
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

    private string BuildAllowedHeaders()
    {
        if (string.IsNullOrWhiteSpace(_webhookOptions.HeaderName))
        {
            return "content-type";
        }

        return $"content-type,{_webhookOptions.HeaderName}";
    }

    private bool IsAuthorized(HttpRequestData req)
    {
        if (string.IsNullOrWhiteSpace(_webhookOptions.Key) || !_webhookOptions.EnforceKey)
        {
            return true;
        }

        var headerName = string.IsNullOrWhiteSpace(_webhookOptions.HeaderName)
            ? "x-acs-webhook-key"
            : _webhookOptions.HeaderName;

        if (req.Headers.TryGetValues(headerName, out var values)
            && values.Any(v => string.Equals(v, _webhookOptions.Key, StringComparison.Ordinal)))
        {
            return true;
        }

        _logger.LogWarning("Unauthorized ACS webhook attempt rejected (header {HeaderName})", headerName);
        return false;
    }

    private bool TryCreateSubscriptionValidationResponse(
        HttpRequestData req,
        IEnumerable<IncomingEvent> events,
        out HttpResponseData response)
    {
        response = default!;

        var headerEventType = TryGetHeader(req, "aeg-event-type");
        if (string.Equals(headerEventType, "SubscriptionValidation", StringComparison.OrdinalIgnoreCase))
        {
            var validationCode = events.FirstOrDefault()?.Data?.ValidationCode;
            response = BuildValidationResponse(req, validationCode);
            _logger.LogInformation("EventGrid subscription validation requested via header.");
            return true;
        }

        var validationEvent = events.FirstOrDefault(
            e => string.Equals(e.EventType, "Microsoft.EventGrid.SubscriptionValidationEvent", StringComparison.OrdinalIgnoreCase));

        if (validationEvent?.Data?.ValidationCode is not null)
        {
            response = BuildValidationResponse(req, validationEvent.Data.ValidationCode);
            _logger.LogInformation("EventGrid subscription validation event received.");
            return true;
        }

        response = default!;
        return false;
    }

    private HttpResponseData BuildValidationResponse(HttpRequestData req, string? validationCode)
    {
        var validation = _responseFactory.CreateJson(req, HttpStatusCode.OK, new { validationResponse = validationCode });
        validation.Headers.Add("Access-Control-Allow-Origin", "*");
        validation.Headers.Add("Access-Control-Allow-Headers", BuildAllowedHeaders());
        return validation;
    }

    private static string? TryGetHeader(HttpRequestData req, string headerName)
    {
        return req.Headers.TryGetValues(headerName, out var values)
            ? values.FirstOrDefault()
            : null;
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

    private static string? DecodeBase64Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var padded = value.Length % 4 == 0 ? value : value.PadRight(value.Length + (4 - value.Length % 4), '=');
            var bytes = Convert.FromBase64String(padded);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

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
