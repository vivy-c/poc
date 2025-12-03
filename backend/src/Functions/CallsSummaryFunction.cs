using System.Net;
using CallTranscription.Functions.Common;
using CallTranscription.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CallTranscription.Functions.Functions;

public class CallsSummaryFunction
{
    private readonly CallSessionStore _callSessionStore;
    private readonly CallSummaryService _callSummaryService;
    private readonly ResponseFactory _responseFactory;

    public CallsSummaryFunction(
        CallSessionStore callSessionStore,
        CallSummaryService callSummaryService,
        ResponseFactory responseFactory)
    {
        _callSessionStore = callSessionStore;
        _callSummaryService = callSummaryService;
        _responseFactory = responseFactory;
    }

    [Function("calls-get-summary")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "options", Route = "calls/{callSessionId:guid}/summary")] HttpRequestData req,
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

        var summary = await _callSummaryService.EnsureSummaryAsync(
            callSessionGuid,
            req.FunctionContext.CancellationToken);

        var payload = new
        {
            callSessionId = session.Id,
            status = session.Status,
            startedAtUtc = session.StartedAtUtc,
            endedAtUtc = session.EndedAtUtc,
            transcriptionStartedAtUtc = session.TranscriptionStartedAtUtc,
            startedByDemoUserId = session.StartedByDemoUserId,
            acsGroupId = session.AcsGroupId,
            callConnectionId = session.CallConnectionId,
            participants = session.Participants.Select(p => new
            {
                p.Id,
                p.DemoUserId,
                p.DisplayName,
                p.AcsIdentity
            }),
            summaryStatus = summary is null ? "pending" : "ready",
            summary = summary?.Summary,
            keyPoints = summary?.KeyPoints ?? Array.Empty<string>(),
            actionItems = summary?.ActionItems ?? Array.Empty<string>(),
            summaryGeneratedAtUtc = summary?.GeneratedAtUtc,
            summarySource = summary?.Source
        };

        var ok = _responseFactory.CreateJson(req, HttpStatusCode.OK, payload);
        ok.Headers.Add("Access-Control-Allow-Origin", "*");
        return ok;
    }
}
