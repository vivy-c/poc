using System.Net;
using CallTranscription.Functions.Common;
using CallTranscription.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CallTranscription.Functions.Functions;

public class CallsTranscriptFunction
{
    private readonly CallSessionStore _callSessionStore;
    private readonly TranscriptStore _transcriptStore;
    private readonly ResponseFactory _responseFactory;

    public CallsTranscriptFunction(
        CallSessionStore callSessionStore,
        TranscriptStore transcriptStore,
        ResponseFactory responseFactory)
    {
        _callSessionStore = callSessionStore;
        _transcriptStore = transcriptStore;
        _responseFactory = responseFactory;
    }

    [Function("calls-get-transcript")]
    public HttpResponseData Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "options", Route = "calls/{callSessionId:guid}/transcript")] HttpRequestData req,
        string callSessionId)
    {
        if (string.Equals(req.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            var preflight = req.CreateResponse(HttpStatusCode.NoContent);
            preflight.Headers.Add("Access-Control-Allow-Origin", "*");
            preflight.Headers.Add("Access-Control-Allow-Methods", "GET,OPTIONS");
            preflight.Headers.Add("Access-Control-Allow-Headers", "content-type");
            return preflight;
        }

        if (!Guid.TryParse(callSessionId, out var callSessionGuid))
        {
            var bad = _responseFactory.CreateJson(
                req,
                HttpStatusCode.BadRequest,
                new { error = "Invalid call session id." });
            bad.Headers.Add("Access-Control-Allow-Origin", "*");
            return bad;
        }

        var session = _callSessionStore.Get(callSessionGuid);
        if (session is null)
        {
            var notFound = _responseFactory.CreateJson(
                req,
                HttpStatusCode.NotFound,
                new { error = "Call session not found." });
            notFound.Headers.Add("Access-Control-Allow-Origin", "*");
            return notFound;
        }

        var segments = _transcriptStore.GetByCallSession(callSessionGuid);
        var payload = new
        {
            callSessionId = session.Id,
            status = session.Status,
            startedAtUtc = session.StartedAtUtc,
            endedAtUtc = session.EndedAtUtc,
            transcriptionStartedAtUtc = session.TranscriptionStartedAtUtc,
            acsGroupId = session.AcsGroupId,
            callConnectionId = session.CallConnectionId,
            segments = segments.Select(s => new
            {
                s.Id,
                s.CallSessionId,
                s.SpeakerDemoUserId,
                s.SpeakerAcsIdentity,
                s.SpeakerDisplayName,
                s.Text,
                s.OffsetSeconds,
                s.DurationSeconds,
                s.CreatedAtUtc,
                s.Source
            })
        };

        var ok = _responseFactory.CreateJson(req, HttpStatusCode.OK, payload);
        ok.Headers.Add("Access-Control-Allow-Origin", "*");
        return ok;
    }
}
