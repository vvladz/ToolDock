using Microsoft.Extensions.Logging;

namespace ToolDock;

internal static class Runner
{
    internal static async Task<int> RunAsync(Paths paths, Guid runId, bool console,
        Func<StartupResult, Task> acknowledge, CancellationToken cancellation)
    {
        using var heldLock = StateStore.TryLock(paths);
        if (heldLock is null) { await acknowledge(new(true, "already running")); return 0; }
        using var stopEvent = Native.CreateStopEvent(paths.EventName(runId));
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var monitor = MonitorStopAsync(stopEvent, stopping);
        var state = new RunnerState(Environment.ProcessId, DateTimeOffset.UtcNow, null, null, runId);
        Logging? logging = null;
        var acknowledged = false;
        var result = 1;
        try
        {
            StateStore.Write(paths, state);
            var snapshot = LaunchSnapshot.Load(paths);
            logging = new Logging(paths, console);
            var logger = logging.Logger;
            while (true)
            {
                stopping.Token.ThrowIfCancellationRequested();
                logger.LogInformation("starting {Executable}", snapshot.Executable);
                var exit = await AttemptAsync(snapshot, logger, stopEvent, stopping.Token, async () =>
                {
                    if (!acknowledged) { await acknowledge(new(true, "started")); acknowledged = true; }
                });
                logger.LogInformation("daemon exited code={ExitCode}", exit);
                stopping.Token.ThrowIfCancellationRequested();
                if (snapshot.Restart == "never" || (snapshot.Restart == "on-failure" && exit == 0))
                { result = exit == 0 ? 0 : 1; break; }
                logger.LogInformation("retry in {Seconds}s", snapshot.Delay.TotalSeconds);
                await Task.Delay(snapshot.Delay, stopping.Token);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested || Native.Signaled(stopEvent))
        {
            result = 0;
            if (!acknowledged) await acknowledge(new(false, "Startup was cancelled."));
            logging?.Logger.LogInformation("stopped");
        }
        catch (Exception error)
        {
            logging?.Logger.LogError("{Text:l}", error.Message);
            if (!acknowledged) await acknowledge(new(false, error.Message));
            else if (console) Console.Error.WriteLine(error.Message);
        }
        finally
        {
            stopping.Cancel();
            await monitor;
            try { StateStore.Write(paths, state with { FinishedAtUtc = DateTimeOffset.UtcNow, ExitCode = result }); }
            catch (Exception error)
            {
                logging?.Logger.LogError("Cannot save runner result: {Text:l}", error.Message);
                throw;
            }
            finally { logging?.Dispose(); }
        }
        return result;
    }

    private static async Task MonitorStopAsync(KernelHandle handle, CancellationTokenSource stopping)
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                if (Native.Signaled(handle)) { stopping.Cancel(); return; }
                await Task.Delay(20, stopping.Token);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private static async Task<uint> AttemptAsync(LaunchSnapshot snapshot, ILogger logger,
        KernelHandle stop, CancellationToken token, Func<Task> started)
    {
        using var job = Native.CreateJob();
        using var stdout = new OutputPipe();
        using var stderr = new OutputPipe();
        using var input = File.OpenHandle(@"\\.\NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Native.Check(Native.SetHandleInformation(input.DangerousGetHandle(), 1, 1));
        using var drain = new CancellationTokenSource();
        Task output = Task.CompletedTask;
        ChildProcess? child = null;
        try
        {
            token.ThrowIfCancellationRequested();
            child = Native.CreateProcess(snapshot.Executable, snapshot.Arguments, snapshot.Directory, snapshot.Environment,
                job, [input.DangerousGetHandle(), stdout.WriterHandle, stderr.WriterHandle]);
            stdout.CloseWriter();
            stderr.CloseWriter();
            output = Task.WhenAll(stdout.PumpAsync(logger, "stdout", drain.Token), stderr.PumpAsync(logger, "stderr", drain.Token));
            if (Native.Signaled(stop)) throw new OperationCanceledException(token);
            token.ThrowIfCancellationRequested();
            child.Resume();
            await started();
            var exited = child.WaitAsync(token);
            if (await Task.WhenAny(exited, output) == output) await output;
            await exited;
            return child.ExitCode;
        }
        finally
        {
            try { await Native.EmptyJobAsync(job); }
            finally
            {
                stdout.CloseWriter();
                stderr.CloseWriter();
                drain.CancelAfter(Settings.DrainTimeout);
                try { await output; }
                finally { child?.Dispose(); }
            }
        }
    }
}

internal sealed record StartupResult(bool Success, string Message);
