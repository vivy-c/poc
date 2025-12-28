using System.Net;
using CallTranscription.Functions.Common;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CallTranscription.Functions.Functions;

public class HealthCheckFunction
{
    private readonly ILogger _logger;
    private readonly ResponseFactory _responseFactory;

    public HealthCheckFunction(ILoggerFactory loggerFactory, ResponseFactory responseFactory)
    {
        _logger = loggerFactory.CreateLogger<HealthCheckFunction>();
        _responseFactory = responseFactory;
    }

    [Function("health")]
    public HttpResponseData Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req)
    {
        _logger.LogInformation("Health check invoked at {Utc}", DateTime.UtcNow);

        var payload = new
        {
            status = "ok",
            service = "CallTranscription.Functions",
            utc = DateTime.UtcNow,
        };

        return _responseFactory.CreateJson(req, HttpStatusCode.OK, payload);
    }
}
