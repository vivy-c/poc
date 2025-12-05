using System.Net;
using System.Text.Json;
using CallTranscription.Functions.Common;
using CallTranscription.Functions.Models;
using CallTranscription.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CallTranscription.Functions.Functions;

public class CallsJoinFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DemoUserStore _demoUserStore;
    private readonly CallSessionStore _callSessionStore;
    private readonly AcsIdentityService _acsIdentityService;
    private readonly AcsCallService _acsCallService;
    private readonly ResponseFactory _responseFactory;
    private readonly ILogger _logger;

    public CallsJoinFunction(
        DemoUserStore demoUserStore,
        CallSessionStore callSessionStore,
        AcsIdentityService acsIdentityService,
        AcsCallService acsCallService,
        ResponseFactory responseFactory,
        ILoggerFactory loggerFactory)
    {
        _demoUserStore = demoUserStore;
        _callSessionStore = callSessionStore;
        _acsIdentityService = acsIdentityService;
        _acsCallService = acsCallService;
        _responseFactory = responseFactory;
        _logger = loggerFactory.CreateLogger<CallsJoinFunction>();
    }

    [Function("calls-join")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", "options", Route = "calls/{callSessionId}/join")] HttpRequestData req,
        string callSessionId)
    {
        if (string.Equals(req.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            var preflight = req.CreateResponse(HttpStatusCode.NoContent);
            preflight.Headers.Add("Access-Control-Allow-Origin", "*");
            preflight.Headers.Add("Access-Control-Allow-Methods", "POST,OPTIONS");
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

        var request = await JsonSerializer.DeserializeAsync<JoinCallRequest>(req.Body, JsonOptions);
        if (request is null || string.IsNullOrWhiteSpace(request.DemoUserId))
        {
            var bad = _responseFactory.CreateJson(
                req,
                HttpStatusCode.BadRequest,
                new { error = "demoUserId is required." });
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

        var demoUser = _demoUserStore.GetById(request.DemoUserId);
        if (demoUser is null)
        {
            var notFound = _responseFactory.CreateJson(
                req,
                HttpStatusCode.NotFound,
                new { error = $"Unknown demo user '{request.DemoUserId}'." });
            notFound.Headers.Add("Access-Control-Allow-Origin", "*");
            return notFound;
        }

        string acsIdentity;
        try
        {
            acsIdentity = await _acsIdentityService.EnsureIdentityAsync(demoUser, req.FunctionContext.CancellationToken);
            _demoUserStore.AssignAcsIdentity(demoUser.Id, acsIdentity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision ACS identity for join request {DemoUserId}", request.DemoUserId);
            var errResp = _responseFactory.CreateJson(
                req,
                HttpStatusCode.InternalServerError,
                new { error = "Unable to provision ACS identity." });
            errResp.Headers.Add("Access-Control-Allow-Origin", "*");
            return errResp;
        }

        string acsToken;
        DateTimeOffset tokenExpiresOn;
        try
        {
            var tokenResult = await _acsIdentityService.IssueVoipTokenAsync(acsIdentity, req.FunctionContext.CancellationToken);
            acsToken = tokenResult.Token;
            tokenExpiresOn = tokenResult.ExpiresOn;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to issue ACS token for join by {DemoUserId}", request.DemoUserId);
            var errResp = _responseFactory.CreateJson(
                req,
                HttpStatusCode.InternalServerError,
                new { error = "Unable to issue ACS token." });
            errResp.Headers.Add("Access-Control-Allow-Origin", "*");
            return errResp;
        }

        var participant = new CallParticipant(Guid.NewGuid(), demoUser.Id, demoUser.DisplayName, acsIdentity);

        var updated = _callSessionStore.AddParticipants(callSessionGuid, new[] { participant });
        if (updated is null)
        {
            var gone = _responseFactory.CreateJson(
                req,
                HttpStatusCode.NotFound,
                new { error = "Call session not found." });
            gone.Headers.Add("Access-Control-Allow-Origin", "*");
            return gone;
        }

        _ = _acsCallService.TryAddParticipantAsync(
            updated.Id,
            updated.CallConnectionId,
            participant,
            req.FunctionContext.CancellationToken);

        var payload = new
        {
            callSessionId = updated.Id,
            acsGroupId = updated.AcsGroupId,
            callConnectionId = updated.CallConnectionId,
            acsToken,
            acsTokenExpiresOn = tokenExpiresOn,
            acsIdentity,
            participants = updated.Participants.Select(p => new
            {
                p.Id,
                p.DemoUserId,
                p.DisplayName,
                p.AcsIdentity
            })
        };

        var ok = _responseFactory.CreateJson(req, HttpStatusCode.OK, payload);
        ok.Headers.Add("Access-Control-Allow-Origin", "*");
        return ok;
    }

    private sealed record JoinCallRequest(string DemoUserId);
}
