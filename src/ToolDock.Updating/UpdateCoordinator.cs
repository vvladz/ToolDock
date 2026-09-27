using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using ToolDock.Common;

namespace ToolDock.Updating;

public sealed class UpdateCoordinator(ToolDockPaths paths, ILoggerFactory loggerFactory)
{
    internal HttpClient? HttpClient { get; init; }
    internal Func<string, CancellationToken, Task<string>>? Reconcile { get; init; }
    internal INotificationSender? Notifications { get; init; }

    public UpdateResult Run(CancellationToken cancellationToken = default)
    {
        using var mutex = new Mutex(false, paths.UpdateMutexName);
        var ownsMutex = false;
        var result = new UpdateResult(UpdateStatus.Failed, Failed: 1);
        var log = loggerFactory.CreateLogger<UpdateCoordinator>();
        using var attempt = new AttemptLog(loggerFactory);
        var notifications = new UpdateNotifications(paths, Notifications ?? new NotificationSender(paths, log), log);
        UpdateEngine? engine = null;
        try
        {
            try { ownsMutex = mutex.WaitOne(TimeSpan.Zero); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex) return new UpdateResult(UpdateStatus.AlreadyRunning);
            log.LogInformation("Update started");
            using var ownedHttp = HttpClient is null ? new HttpClient { Timeout = TimeSpan.FromMinutes(10) } : null;
            var http = HttpClient ?? ownedHttp!;
            if (!http.DefaultRequestHeaders.UserAgent.Any()) http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ToolDock", "1.0"));
            engine = new UpdateEngine(paths, attempt, http,
                Reconcile ?? ((request, token) => new StarterClient(paths).SendAsync(request, TimeSpan.FromMinutes(2), token)), notifications);
            result = engine.RunAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new UpdateResult(UpdateStatus.Cancelled, engine?.Checked ?? 0, engine?.Updated ?? 0, engine?.Failed ?? 0);
        }
        catch (Exception exception)
        {
            attempt.CreateLogger<UpdateCoordinator>().LogError(exception, "Update failed");
            result = new UpdateResult(UpdateStatus.Failed, engine?.Checked ?? 0, engine?.Updated ?? 0, (engine?.Failed ?? 0) + 1);
            notifications.FailureAsync(null, exception, attempt.Text).GetAwaiter().GetResult();
        }
        finally
        {
            if (ownsMutex)
            {
                try
                {
                    log.LogInformation(new EventId(1001, "UpdateOutcome"),
                        "Update finished: status={Status} checked={Checked} updated={Updated} failed={Failed}",
                        result.Status, result.Checked, result.Updated, result.Failed);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        return result;
    }
}
