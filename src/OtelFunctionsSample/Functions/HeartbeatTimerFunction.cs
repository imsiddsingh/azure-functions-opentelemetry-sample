using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace OtelFunctionsSample.Functions;

/// <summary>
/// A timer-triggered function that fires every minute and writes a few logs at
/// different severities. Use it to confirm that telemetry keeps flowing without
/// you having to send HTTP requests.
///
/// Note: timer triggers need a storage account for the singleton lock. Locally
/// that means running Azurite with AzureWebJobsStorage=UseDevelopmentStorage=true
/// (see the README). If you don't want to run storage, just use the Hello HTTP
/// function and ignore this one.
/// </summary>
public class HeartbeatTimerFunction(ILogger<HeartbeatTimerFunction> logger)
{
    private readonly ILogger<HeartbeatTimerFunction> _logger = logger;

    // Every minute, on the minute.
    [Function("Heartbeat")]
    public void Run([TimerTrigger("0 */1 * * * *")] TimerInfo timer)
    {
        _logger.LogInformation(
            "Heartbeat at {TimestampUtc}. Next run: {NextRun}",
            DateTimeOffset.UtcNow,
            timer.ScheduleStatus?.Next);
    }
}
