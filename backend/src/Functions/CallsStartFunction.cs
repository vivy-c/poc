using System.Net;
using System.Text.Json;
using CallTranscription.Functions.Common;
using CallTranscription.Functions.Models;
using CallTranscription.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CallTranscription.Functions.Functions;

public class CallsStartFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DemoUserStore _demoUserStore;
    private readonly CallSessionStore _callSessionStore;
    private readonly AcsIdentityService _acsIdentityService;
    private readonly ResponseFactory _responseFactory;
    private readonly ILogger _logger;

    public CallsStartFunction(
        DemoUserStore demoUserStore,
        CallSessionStore callSessionStore,
        AcsIdentityService acsIdentityService,
        ResponseFactory responseFactory,
        ILoggerFactory loggerFactory)
    {
        _demoUserStore = demoUserStore;
        _callSessionStore = callSessionStore;
        _acsIdentityService = acsIdentityService;
        _responseFactory = responseFactory;
        _logger = loggerFactory.CreateLogger<CallsStartFunction>();
    }

    [Function("calls-start")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", "options", Route = "calls/start")] HttpRequestData req)
    {
        if (string.Equals(req.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            var preflight = req.CreateResponse(HttpStatusCode.NoContent);
            preflight.Headers.Add("Access-Control-Allow-Origin", "*");
            preflight.Headers.Add("Access-Control-Allow-Methods", "POST,OPTIONS");
            preflight.Headers.Add("Access-Control-Allow-Headers", "content-type");
            return preflight;
        }
        var request = await JsonSerializer.DeserializeAsync<StartCallRequest>(req.Body, JsonOptions);
        if (request is null || string.IsNullOrWhiteSpace(request.DemoUserId))
        {
            var bad = _responseFactory.CreateJson(
                req,
                HttpStatusCode.BadRequest,
                new { error = "demoUserId is required." });
            bad.Headers.Add("Access-Control-Allow-Origin", "*");
            return bad;
        }

        var initiator = _demoUserStore.GetById(request.DemoUserId);
        if (initiator is null)
        {
            var notFound = _responseFactory.CreateJson(
                req,
                HttpStatusCode.NotFound,
                new { error = $"Unknown demo user '{request.DemoUserId}'." });
            notFound.Headers.Add("Access-Control-Allow-Origin", "*");
            return notFound;
        }

        var participantIds = request.ParticipantIds?.Distinct(StringComparer.OrdinalIgnoreCase).Where(id => id != initiator.Id).ToList()
            ?? new List<string>();

        var participants = new List<CallParticipant>();
        string acsIdentity;

        try
        {
            acsIdentity = await _acsIdentityService.EnsureIdentityAsync(initiator, req.FunctionContext.CancellationToken);
            _demoUserStore.AssignAcsIdentity(initiator.Id, acsIdentity);

            participants.Add(new CallParticipant(Guid.NewGuid(), initiator.Id, initiator.DisplayName, acsIdentity));

            foreach (var participantId in participantIds)
            {
                var participantUser = _demoUserStore.GetById(participantId);
                if (participantUser is null)
                {
                    _logger.LogWarning("Requested participant {ParticipantId} not found", participantId);
                    continue;
                }

                var participantIdentity = await _acsIdentityService.EnsureIdentityAsync(participantUser, req.FunctionContext.CancellationToken);
                _demoUserStore.AssignAcsIdentity(participantUser.Id, participantIdentity);

                participants.Add(new CallParticipant(Guid.NewGuid(), participantUser.Id, participantUser.DisplayName, participantIdentity));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision ACS identity for call start by {DemoUserId}", request.DemoUserId);
            var errResp = _responseFactory.CreateJson(
                req,
                HttpStatusCode.InternalServerError,
                new { error = "Unable to start call (identity provisioning failed)." });
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
            _logger.LogError(ex, "Failed to issue ACS token for {DemoUserId}", request.DemoUserId);
            var errResp = _responseFactory.CreateJson(
                req,
                HttpStatusCode.InternalServerError,
                new { error = "Unable to issue ACS token." });
            errResp.Headers.Add("Access-Control-Allow-Origin", "*");
            return errResp;
        }

        var acsGroupId = Guid.NewGuid().ToString();
        var callSession = await _callSessionStore.CreateAsync(
            initiator.Id,
            acsGroupId,
            participants,
            callConnectionId: null,
            cancellationToken: req.FunctionContext.CancellationToken);

        var payload = new
        {
            callSessionId = callSession.Id,
            acsGroupId,
            callConnectionId = callSession.CallConnectionId,
            status = callSession.Status,
            acsToken,
            acsTokenExpiresOn = tokenExpiresOn,
            acsIdentity,
            participants = callSession.Participants.Select(p => new
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

    private sealed record StartCallRequest(string DemoUserId, IEnumerable<string>? ParticipantIds);
}
