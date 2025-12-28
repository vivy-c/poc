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
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "demo-users/init")] HttpRequestData req)
    {
        var request = await JsonSerializer.DeserializeAsync<InitDemoUserRequest>(req.Body, JsonOptions);
        if (request is null || string.IsNullOrWhiteSpace(request.DemoUserId))
        {
            return _responseFactory.CreateJson(
                req,
                HttpStatusCode.BadRequest,
                new { error = "demoUserId is required." });
        }

        var demoUser = _demoUserStore.GetById(request.DemoUserId);
        if (demoUser is null)
        {
            return _responseFactory.CreateJson(
                req,
                HttpStatusCode.NotFound,
                new { error = $"Unknown demo user '{request.DemoUserId}'." });
        }

        string acsIdentity;
        try
        {
            acsIdentity = await _acsIdentityService.EnsureIdentityAsync(demoUser, req.FunctionContext.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision ACS identity for demo user {DemoUserId}", request.DemoUserId);
            return _responseFactory.CreateJson(
                req,
                HttpStatusCode.InternalServerError,
                new { error = "Unable to provision ACS identity." });
        }

        _demoUserStore.AssignAcsIdentity(demoUser.Id, acsIdentity);

        var payload = new
        {
            demoUserId = demoUser.Id,
            displayName = demoUser.DisplayName,
            role = demoUser.Role,
            acsIdentity
        };

        return _responseFactory.CreateJson(req, HttpStatusCode.OK, payload);
    }

    private sealed record InitDemoUserRequest(string DemoUserId);
}
