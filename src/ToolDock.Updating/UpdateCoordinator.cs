using Microsoft.Extensions.Logging;
using ToolDock.Common;

namespace ToolDock.Updating;

public sealed class UpdateCoordinator(ToolDockPaths paths, ILoggerFactory loggerFactory)
{
    private const string MutexName = @"Local\ToolDock.Update";

    public UpdateResult Run(CancellationToken cancellationToken = default)
    {
        using var mutex = new Mutex(initiallyOwned: false, MutexName);
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (!ownsMutex)
            {
                return new UpdateResult(UpdateStatus.AlreadyRunning);
            }

            return new UpdateEngine(paths, loggerFactory)
                .RunAsync(cancellationToken)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new UpdateResult(UpdateStatus.Cancelled);
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger<UpdateCoordinator>().LogError(exception, "Update failed");
            return new UpdateResult(UpdateStatus.Failed, Failed: 1);
        }
        finally
        {
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }
        }
    }
}
