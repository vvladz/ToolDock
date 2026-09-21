using System.Diagnostics;
using ToolDock.Common.Logging;
using ToolDock.Starter.Jobs;

namespace ToolDock.Starter.Processes;

internal sealed class ManagedToolProcess : IAsyncDisposable
{
    private readonly string _name;
    private readonly JobObject _job;
    private readonly Process _process;
    private readonly StreamReader _stdout;
    private readonly StreamReader _stderr;
    private readonly RotatingFileWriter _log;
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private bool _jobClosed;
    private bool _disposed;

    private ManagedToolProcess(
        string name,
        JobObject job,
        LaunchedProcess launched,
        RotatingFileWriter log)
    {
        _name = name;
        _job = job;
        _process = launched.Process;
        _stdout = launched.StandardOutput;
        _stderr = launched.StandardError;
        _log = log;
        _log.WriteLine(Line("SYS", $"process started pid={_process.Id}"));
        _stdoutPump = PumpAsync(_stdout, "OUT");
        _stderrPump = PumpAsync(_stderr, "ERR");
    }

    public int ProcessId => _process.Id;
    public bool IsRunning => !_process.HasExited;
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    public static ManagedToolProcess Start(
        string name,
        string executable,
        IReadOnlyList<string> arguments,
        string logPath)
    {
        var job = new JobObject();
        var log = new RotatingFileWriter(logPath);
        try
        {
            var launched = NativeProcessLauncher.StartSuspendedInJob(executable, arguments, job);
            return new ManagedToolProcess(name, job, launched, log);
        }
        catch
        {
            job.Dispose();
            log.Dispose();
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (_jobClosed)
        {
            return;
        }

        _log.WriteLine(Line("SYS", "stop requested"));
        _job.Dispose();
        _jobClosed = true;
        try
        {
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Timed out waiting for {_name} job to terminate.");
        }

        await Task.WhenAll(_stdoutPump, _stderrPump).WaitAsync(TimeSpan.FromSeconds(5));
        _log.WriteLine(Line("SYS", $"process exited pid={_process.Id} code={_process.ExitCode}"));
    }

    private async Task PumpAsync(StreamReader reader, string stream)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                _log.WriteLine(Line(stream, line));
            }
        }
        catch (Exception exception) when (_jobClosed && exception is IOException or ObjectDisposedException)
        {
            // Closing the Job Object can close inherited pipe handles while a read is pending.
        }
    }

    private static string Line(string stream, string message)
        => $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {stream} {message}";

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_jobClosed)
        {
            try
            {
                await StopAsync();
            }
            catch
            {
                // Best-effort cleanup while the host is shutting down.
            }
        }

        _stdout.Dispose();
        _stderr.Dispose();
        _process.Dispose();
        _log.Dispose();
    }
}
