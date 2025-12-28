using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Http;

namespace CallTranscription.Functions.Common;

public class ResponseFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public HttpResponseData CreateJson<T>(HttpRequestData request, HttpStatusCode statusCode, T payload)
    {
        var response = request.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json");
        response.WriteString(JsonSerializer.Serialize(payload, JsonOptions));
        return response;
    }
}
