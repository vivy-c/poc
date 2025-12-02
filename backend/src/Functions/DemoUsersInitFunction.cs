using System.Net;
using System.Text.Json;
using CallTranscription.Functions.Common;
using CallTranscription.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CallTranscription.Functions.Functions;

public class DemoUsersInitFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DemoUserStore _demoUserStore;
    private readonly AcsIdentityService _acsIdentityService;
    private readonly ResponseFactory _responseFactory;
    private readonly ILogger _logger;

    public DemoUsersInitFunction(
        DemoUserStore demoUserStore,
        AcsIdentityService acsIdentityService,
        ResponseFactory responseFactory,
        ILoggerFactory loggerFactory)
    {
        _demoUserStore = demoUserStore;
        _acsIdentityService = acsIdentityService;
        _responseFactory = responseFactory;
        _logger = loggerFactory.CreateLogger<DemoUsersInitFunction>();
    }

    [Function("demo-users-init")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", "get", "options", Route = "demo-users/init")] HttpRequestData req)
    {
        if (string.Equals(req.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            var preflight = req.CreateResponse(HttpStatusCode.NoContent);
            preflight.Headers.Add("Access-Control-Allow-Origin", "*");
            preflight.Headers.Add("Access-Control-Allow-Methods", "POST,GET,OPTIONS");
            preflight.Headers.Add("Access-Control-Allow-Headers", "content-type");
            return preflight;
        }

        string? demoUserId = null;
        if (string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            var query = req.Url.Query.TrimStart('?');
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                var key = Uri.UnescapeDataString(kv[0]);
                var value = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
                if (string.Equals(key, "demoUserId", StringComparison.OrdinalIgnoreCase))
                {
                    demoUserId = value;
                    break;
                }
            }
        }
        else
        {
            var request = await JsonSerializer.DeserializeAsync<InitDemoUserRequest>(req.Body, JsonOptions);
            demoUserId = request?.DemoUserId;
        }

        if (string.IsNullOrWhiteSpace(demoUserId))
        {
            var bad = _responseFactory.CreateJson(
                req,
                HttpStatusCode.BadRequest,
                new { error = "demoUserId is required." });
            bad.Headers.Add("Access-Control-Allow-Origin", "*");
            return bad;
        }

        var demoUser = _demoUserStore.GetById(demoUserId);
        if (demoUser is null)
        {
            var notFound = _responseFactory.CreateJson(
                req,
                HttpStatusCode.NotFound,
                new { error = $"Unknown demo user '{demoUserId}'." });
            notFound.Headers.Add("Access-Control-Allow-Origin", "*");
            return notFound;
        }

        string acsIdentity;
        try
        {
            acsIdentity = await _acsIdentityService.EnsureIdentityAsync(demoUser, req.FunctionContext.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision ACS identity for demo user {DemoUserId}", demoUserId);
            var errResp = _responseFactory.CreateJson(
                req,
                HttpStatusCode.InternalServerError,
                new { error = "Unable to provision ACS identity." });
            errResp.Headers.Add("Access-Control-Allow-Origin", "*");
            return errResp;
        }

        _demoUserStore.AssignAcsIdentity(demoUser.Id, acsIdentity);

        var payload = new
        {
            demoUserId = demoUser.Id,
            displayName = demoUser.DisplayName,
            role = demoUser.Role,
            acsIdentity
        };

        var ok = _responseFactory.CreateJson(req, HttpStatusCode.OK, payload);
        ok.Headers.Add("Access-Control-Allow-Origin", "*");
        return ok;
    }

    private sealed record InitDemoUserRequest(string DemoUserId);
}
