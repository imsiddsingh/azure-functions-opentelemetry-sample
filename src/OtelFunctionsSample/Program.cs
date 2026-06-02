using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ---------------------------------------------------------------------------
// Azure Functions (.NET isolated) + OpenTelemetry -> Azure Monitor
// ---------------------------------------------------------------------------
// This replaces the classic Application Insights SDK wiring you may have today:
//
//   // OLD (App Insights SDK):
//   builder.Services.AddApplicationInsightsTelemetryWorkerService();
//   builder.ConfigureFunctionsApplicationInsights();
//
// ...with the vendor-neutral OpenTelemetry pipeline below. Your application
// code does NOT change: you keep using ILogger<T>, and the host keeps emitting
// request/dependency telemetry. Only the export plumbing changes.
// ---------------------------------------------------------------------------

var builder = FunctionsApplication.CreateBuilder(args);

// 1) Route ILogger output into OpenTelemetry. Every _logger.LogXxx(...) call in
//    your functions becomes an OTel LogRecord that the exporter ships to
//    Application Insights (it lands in the AppTraces table).
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
});

// 2) Configure the OpenTelemetry SDK with the Functions worker defaults
//    (auto-instrumentation for the host/trigger pipeline) and export traces,
//    logs, and metrics to Azure Monitor.
builder.Services
    .AddOpenTelemetry()
    .UseFunctionsWorkerDefaults()
    .UseAzureMonitorExporter(options =>
    {
        // The connection string identifies the target Application Insights
        // resource. Provided via the APPLICATIONINSIGHTS_CONNECTION_STRING
        // app setting (see local.settings.json.example).
        options.ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];

        // ── Microsoft Entra (AAD) authentication — READ Gotcha #1 in the README ──
        // When a credential is set, the exporter authenticates to the ingestion
        // endpoint with that identity INSTEAD of the connection string's
        // instrumentation key. That identity MUST hold the
        // "Monitoring Metrics Publisher" role on the Application Insights
        // resource, or every export is silently rejected (HTTP 403) — your
        // console logs still print, but NOTHING is ingested.
        //
        // We enable it only when NOT running locally, so `func start` / F5 works
        // with just the connection string (instrumentation-key auth, no role
        // assignment needed to try the sample). In Azure, the Function App's
        // managed identity is used and must be granted the role.
        if (!IsRunningLocally(builder.Configuration))
        {
            options.Credential = new Azure.Identity.DefaultAzureCredential();
        }
    });

builder.Build().Run();

static bool IsRunningLocally(IConfiguration config) =>
    string.Equals(
        config["AZURE_FUNCTIONS_ENVIRONMENT"] ?? Environment.GetEnvironmentVariable("AZURE_FUNCTIONS_ENVIRONMENT"),
        "Development",
        StringComparison.OrdinalIgnoreCase);
