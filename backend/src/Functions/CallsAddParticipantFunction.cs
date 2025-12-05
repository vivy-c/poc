using System.Net;
using System.Text.Json;
using CallTranscription.Functions.Common;
using CallTranscription.Functions.Models;
using CallTranscription.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CallTranscription.Functions.Functions;

public class CallsAddParticipantFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DemoUserStore _demoUserStore;
    private readonly CallSessionStore _callSessionStore;
    private readonly AcsIdentityService _acsIdentityService;
    private readonly AcsCallService _acsCallService;
    private readonly ResponseFactory _responseFactory;
    private readonly ILogger _logger;

    public CallsAddParticipantFunction(
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
        _logger = loggerFactory.CreateLogger<CallsAddParticipantFunction>();
    }

    [Function("calls-add-participant")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", "options", Route = "calls/{callSessionId:guid}/add-participant")] HttpRequestData req,
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

        var request = await JsonSerializer.DeserializeAsync<AddParticipantsRequest>(req.Body, JsonOptions);
        var requestedIds = request?.ParticipantIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        if (requestedIds.Count == 0)
        {
            var bad = _responseFactory.CreateJson(
                req,
                HttpStatusCode.BadRequest,
                new { error = "participantIds is required." });
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

        var existingDemoUserIds = new HashSet<string>(
            session.Participants.Select(p => p.DemoUserId),
            StringComparer.OrdinalIgnoreCase);

        var added = new List<AddedParticipant>();
        var skipped = new List<object>();

        foreach (var participantId in requestedIds)
        {
            if (existingDemoUserIds.Contains(participantId))
            {
                skipped.Add(new { demoUserId = participantId, reason = "already in call" });
                continue;
            }

            var demoUser = _demoUserStore.GetById(participantId);
            if (demoUser is null)
            {
                _logger.LogWarning("Requested participant {ParticipantId} not found", participantId);
                skipped.Add(new { demoUserId = participantId, reason = "unknown demo user" });
                continue;
            }

            string acsIdentity;
            try
            {
                acsIdentity = await _acsIdentityService.EnsureIdentityAsync(demoUser, req.FunctionContext.CancellationToken);
                _demoUserStore.AssignAcsIdentity(demoUser.Id, acsIdentity);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to ensure ACS identity for participant {ParticipantId} on call {CallSessionId}",
                    participantId,
                    callSessionGuid);

                var errResp = _responseFactory.CreateJson(
                    req,
                    HttpStatusCode.InternalServerError,
                    new { error = $"Unable to provision ACS identity for '{participantId}'." });
                errResp.Headers.Add("Access-Control-Allow-Origin", "*");
                return errResp;
            }

            var participant = new CallParticipant(Guid.NewGuid(), demoUser.Id, demoUser.DisplayName, acsIdentity);
            var acsInviteSent = await _acsCallService.TryAddParticipantAsync(
                session.Id,
                session.CallConnectionId,
                participant,
                req.FunctionContext.CancellationToken);

            added.Add(new AddedParticipant(participant, acsInviteSent));
            existingDemoUserIds.Add(participantId);
        }

        if (added.Count == 0)
        {
            var bad = _responseFactory.CreateJson(
                req,
                HttpStatusCode.BadRequest,
                new { error = "No new participants to add.", skipped });
            bad.Headers.Add("Access-Control-Allow-Origin", "*");
            return bad;
        }

        var updated = _callSessionStore.AddParticipants(callSessionGuid, added.Select(a => a.Participant).ToList());
        if (updated is null)
        {
            var gone = _responseFactory.CreateJson(
                req,
                HttpStatusCode.NotFound,
                new { error = "Call session not found." });
            gone.Headers.Add("Access-Control-Allow-Origin", "*");
            return gone;
        }

        var payload = new
        {
            callSessionId = updated.Id,
            acsGroupId = updated.AcsGroupId,
            callConnectionId = updated.CallConnectionId,
            added = added.Select(a => new
            {
                a.Participant.Id,
                a.Participant.DemoUserId,
                a.Participant.DisplayName,
                a.Participant.AcsIdentity,
                acsInviteDispatched = a.AcsInviteSent
            }),
            participants = updated.Participants.Select(p => new
            {
                p.Id,
                p.DemoUserId,
                p.DisplayName,
                p.AcsIdentity
            }),
            skipped
        };

        var ok = _responseFactory.CreateJson(req, HttpStatusCode.OK, payload);
        ok.Headers.Add("Access-Control-Allow-Origin", "*");
        return ok;
    }

    private sealed record AddParticipantsRequest(IEnumerable<string> ParticipantIds);

    private sealed record AddedParticipant(CallParticipant Participant, bool AcsInviteSent);
}
