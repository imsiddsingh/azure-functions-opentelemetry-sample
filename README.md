# Azure Functions + OpenTelemetry → Application Insights (.NET isolated)

A small, **zero-dependency** sample that shows how to send Azure Functions
telemetry (logs + requests + dependencies + traces) to **Application Insights
via OpenTelemetry**, instead of the classic Application Insights SDK.

It exists because migrating an isolated‑worker Function from the App Insights
SDK to OpenTelemetry has a handful of sharp edges that cost real debugging time
— especially around **Entra (AAD) authentication** and **log‑level filtering**.
Those are documented in [Gotchas](#gotchas) so you don't have to rediscover them.

> Runtime: **.NET 10 isolated worker**, Functions **v4**. Triggers: one HTTP, one
> Timer. No Event Hub / Redis / Cosmos / Key Vault needed — clone, paste a
> connection string, run.

---

## Why move to OpenTelemetry?

- **Vendor‑neutral & future‑proof** — the same instrumentation can target Azure
  Monitor today and any OTLP backend (Grafana/Tempo, Jaeger, Honeycomb, …)
  tomorrow by swapping the exporter.
- **One pipeline for logs, traces, and metrics**, with automatic correlation.
- It's the **direction Microsoft is steering** Functions telemetry
  (`telemetryMode: OpenTelemetry`).

Your application code doesn't change — you keep using `ILogger<T>`. Only the
**export plumbing** in `Program.cs` and `host.json` changes.

---

## Before / after

```csharp
// ── BEFORE — classic Application Insights SDK (isolated worker) ──
builder.Services.AddApplicationInsightsTelemetryWorkerService();
builder.ConfigureFunctionsApplicationInsights();
```

```csharp
// ── AFTER — OpenTelemetry exporting to Azure Monitor ──
builder.Logging.AddOpenTelemetry(o =>
{
    o.IncludeFormattedMessage = true;
    o.IncludeScopes = true;
});

builder.Services
    .AddOpenTelemetry()
    .UseFunctionsWorkerDefaults()
    .UseAzureMonitorExporter(options =>
    {
        options.ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        // options.Credential = new DefaultAzureCredential();  // see Gotcha #1
    });
```

Plus one line in `host.json`:

```jsonc
"telemetryMode": "OpenTelemetry"
```

See [`Program.cs`](src/OtelFunctionsSample/Program.cs) and
[`host.json`](src/OtelFunctionsSample/host.json) for the fully commented versions.

---

## Project layout

```
azure-functions-opentelemetry-sample/
├─ README.md
├─ LICENSE
├─ .gitignore
└─ src/OtelFunctionsSample/
   ├─ OtelFunctionsSample.csproj        # packages: Worker.OpenTelemetry + Azure.Monitor exporter
   ├─ Program.cs                        # the OTel wiring (the important bit)
   ├─ host.json                         # telemetryMode + host log levels
   ├─ local.settings.json.example       # copy to local.settings.json and fill in
   └─ Functions/
      ├─ HelloHttpFunction.cs           # HTTP trigger, logs at several severities
      └─ HeartbeatTimerFunction.cs      # Timer trigger (needs storage; optional)
```

---

## Run it locally

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
- An **Application Insights** resource (Workspace‑based is fine) — copy its
  **Connection String**.
- *(Only for the Timer function)* [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite)
  for local storage.

### Steps
```bash
# 1. Copy the settings template and paste your connection string
cd src/OtelFunctionsSample
cp local.settings.json.example local.settings.json
#   edit local.settings.json -> APPLICATIONINSIGHTS_CONNECTION_STRING

# 2. (optional, for the Timer trigger) start Azurite in another terminal
azurite

# 3. Run
func start
#   or: dotnet run
```

Then trigger it:
```bash
curl "http://localhost:7071/api/Hello?name=otel"
```

Locally, telemetry authenticates with the **connection string's instrumentation
key** (no Azure role needed — see Gotcha #1). Within a minute or two it shows up
in Application Insights → **Logs**:

```kusto
union AppRequests, AppTraces
| where TimeGenerated > ago(15m)
| order by TimeGenerated desc
```

---

## Gotchas

These are the things that actually cost time. Read them before you ship.

### #1 — Entra (AAD) auth needs the *Monitoring Metrics Publisher* role

If you set `options.Credential = new DefaultAzureCredential()` (recommended for
production — no ingestion key in config), the exporter authenticates to the
ingestion endpoint with that identity **instead of** the connection string's
instrumentation key.

That identity **must** have the **`Monitoring Metrics Publisher`** role on the
target Application Insights resource. If it doesn't:

- every export is rejected with **HTTP 403**,
- **nothing** is ingested into Application Insights,
- **but your console / debug logs still print** — so it looks like "logging is
  broken in this environment" when it's actually an authorization gap.

This bites in two places:

| Where | Identity used | Fix |
|---|---|---|
| **In Azure** | the Function App's **managed identity** | grant it `Monitoring Metrics Publisher` on the App Insights resource |
| **Local dev** | **your** `az`/Visual Studio login | grant *your* user the role, **or** don't set `Credential` locally (use ikey auth) |

Grant the role:
```bash
az role assignment create \
  --assignee <objectId-of-identity> \
  --role "Monitoring Metrics Publisher" \
  --scope <resourceId-of-the-Application-Insights-component>
```

This sample sidesteps the local case by only enabling `Credential` when
**not** running locally (`AZURE_FUNCTIONS_ENVIRONMENT != Development`), so
`func start` works with just the connection string.

> **If you set a connection string *and* a `Credential`, the credential wins for
> authentication.** The instrumentation key in the connection string is then
> only used to identify the resource, not to authenticate — so a valid key won't
> save you if the role is missing.

### #2 — There are TWO log‑level filters: host vs worker

Logs pass through two independent filters, and **both** must allow a level for
it to be exported:

| Layer | Configured in | Category form | Example |
|---|---|---|---|
| **Worker** (your app code) | app settings / `local.settings.json` | full **namespace + class** | `Logging__LogLevel__OtelFunctionsSample.Functions.HelloHttpFunction` |
| **Host** (the Functions runtime) | `host.json` → `logging.logLevel` | `Function.<FunctionName>` (the `[Function("…")]` name) | `Function.Hello` |

Common trap: you set the **worker** category to `Information`, the log prints to
the console/VS Debug output (worker emitted it) — but it never reaches App
Insights because the **host.json** entry for that function is still at `Warning`.
Keep them in sync. Note the category styles differ: the worker uses your C#
type name; the host uses the function's trigger name.

### #3 — "It logs to the console but not to App Insights"

That combination almost always means **#1 (auth/role)** or **#2 (host level)** —
not your code. The worker writing to stdout and the exporter shipping to Azure
Monitor are two different paths. A log line in the terminal only proves the
worker *emitted* it; it says nothing about whether it was *exported and accepted*.

### #4 — Local console vs Application Insights are not the same view

When debugging from Visual Studio, **worker** logs appear in the **Debug output**
window, while the **Functions host console** is filtered by `host.json` (and in
`OpenTelemetry` mode is quieter by design). Don't judge ingestion by the host
console — verify in App Insights with the queries below.

### #5 — Workspace ingestion transforms can drop data downstream

If your logs reach App Insights but still don't appear, check for a
**Data Collection Rule** of kind `WorkspaceTransforms` on the backing Log
Analytics workspace — an ingestion‑time KQL transform can filter rows
(commonly used to drop successful `AppRequests`/`AppDependencies` for noise/cost
control). That's downstream of everything above.

---

## Verify telemetry (KQL)

Run these in Application Insights → **Logs** (workspace‑based) or the classic
Logs blade:

```kusto
// Logs from your functions (ILogger)
AppTraces
| where TimeGenerated > ago(1h)
| project TimeGenerated, SeverityLevel, Message, AppRoleName
| order by TimeGenerated desc
```
```kusto
// Auto-collected request spans
AppRequests
| where TimeGenerated > ago(1h)
| project TimeGenerated, Name, ResultCode, Success, DurationMs
| order by TimeGenerated desc
```
SeverityLevel mapping: `0` Trace · `1` Information · `2` Warning · `3` Error · `4` Critical.

---

## Deploy to Azure (outline)

1. Create the Function App (.NET 10 isolated) and an Application Insights resource.
2. Give the Function App a **managed identity** and set
   `APPLICATIONINSIGHTS_CONNECTION_STRING` in its app settings.
3. **Grant the identity `Monitoring Metrics Publisher`** on the App Insights
   resource (Gotcha #1). Example Bicep:
   ```bicep
   resource role 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
     name: guid(appInsights.id, functionApp.id, 'MonitoringMetricsPublisher')
     scope: appInsights
     properties: {
       // Monitoring Metrics Publisher
       roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e3f-901f-e34b0fa6c8a4')
       principalId: functionApp.identity.principalId
       principalType: 'ServicePrincipal'
     }
   }
   ```
4. Publish: `func azure functionapp publish <app-name>` (or your CI/CD pipeline).

---

## Extending the sample

- **Custom spans:** create an `ActivitySource`, register it with
  `.WithTracing(t => t.AddSource("MySource"))`, and `StartActivity(...)` in code.
- **Custom metrics:** create a `Meter`, register with
  `.WithMetrics(m => m.AddMeter("MySource"))`, and increment a `Counter<T>`.
- **Different backend:** replace `UseAzureMonitorExporter(...)` with an OTLP
  exporter to point at Grafana/Jaeger/etc. — the function code is unchanged.

---

## License

[MIT](LICENSE). Contributions and issues welcome.
