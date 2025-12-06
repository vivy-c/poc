using CallTranscription.Functions.Common;
using CallTranscription.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Timer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CallTranscription.Functions.Functions;

public class CallSessionCleanupFunction
{
    private readonly CallSessionStore _callSessionStore;
    private readonly AcsTranscriptionService _transcriptionService;
    private readonly FeatureFlagsOptions _features;
    private readonly ILogger _logger;

    public CallSessionCleanupFunction(
        CallSessionStore callSessionStore,
        AcsTranscriptionService transcriptionService,
        IOptions<FeatureFlagsOptions> features,
        ILoggerFactory loggerFactory)
    {
        _callSessionStore = callSessionStore;
        _transcriptionService = transcriptionService;
        _features = features.Value;
        _logger = loggerFactory.CreateLogger<CallSessionCleanupFunction>();
    }

    // Every 15 minutes
    [Function("cleanup-stale-calls")]
    public async Task RunAsync([TimerTrigger("0 */15 * * * *")] TimerInfo timerInfo, FunctionContext context)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Max(5, _features.StaleCallMinutes));
        var staleSessions = await _callSessionStore.FindStaleActiveSessionsAsync(cutoff, context.CancellationToken);
        if (staleSessions.Count == 0)
        {
            return;
        }

        foreach (var session in staleSessions)
        {
            try
            {
                await _callSessionStore.UpdateStatusAsync(session.Id, "Completed", DateTime.UtcNow, context.CancellationToken);
                if (!string.IsNullOrWhiteSpace(session.CallConnectionId))
                {
                    _ = _transcriptionService.TryStopAsync(session, CancellationToken.None);
                }
                _logger.LogInformation("Marked stale call {CallSessionId} as Completed (started {StartedAt})", session.Id, session.StartedAtUtc);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up stale call session {CallSessionId}", session.Id);
            }
        }
    }
}
