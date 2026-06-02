using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace OtelFunctionsSample.Functions;

/// <summary>
/// A minimal HTTP-triggered function. Hitting it produces:
///   - an OTel "request" span (host auto-instrumentation -> AppRequests)
///   - the ILogger entries below (-> AppTraces)
/// all exported to Application Insights via the OpenTelemetry pipeline.
/// </summary>
public class HelloHttpFunction(ILogger<HelloHttpFunction> logger)
{
    private readonly ILogger<HelloHttpFunction> _logger = logger;

    [Function("Hello")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req)
    {
        // Structured logging: {Name} is a property, not string concat. In
        // Application Insights you can filter/aggregate on customDimensions.Name.
        var name = req.Query["name"] ?? "world";
        _logger.LogInformation("Hello endpoint invoked for {Name}", name);

        // These demonstrate the level filter. With the default level at
        // Information, the Debug line is dropped and the Warning line is kept.
        _logger.LogDebug("This debug line is filtered out at the default level");
        _logger.LogWarning("Example warning so you can see severity in AppTraces");

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteStringAsync($"Hello, {name}! This response was traced by OpenTelemetry.");
        return response;
    }
}
