using System.Diagnostics;
using ToolDock.Common.Logging;
using ToolDock.Starter.Jobs;

namespace ToolDock.Starter.Processes;

internal sealed record DaemonExit(string Name, int ProcessId, int ExitCode, string? LastError);

internal sealed class ManagedToolProcess : IAsyncDisposable
{
    private readonly string _name;
    private readonly JobObject _job;
    private readonly Process _process;
    private readonly StreamReader _stdout;
    private readonly StreamReader _stderr;
    private readonly RotatingFileWriter _log;
    private readonly IDisposable? _lease;
    private readonly Action<DaemonExit>? _onUnexpectedExit;
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private readonly object _lifecycle = new();
    private string? _lastError;
    private bool _stopRequested;
    private bool _jobClosed;
    private bool _disposed;

    private ManagedToolProcess(string name, JobObject job, LaunchedProcess launched, RotatingFileWriter log,
        IDisposable? lease, string? activationId, Action<DaemonExit>? onUnexpectedExit)
    {
        _name = name;
        _job = job;
        _process = launched.Process;
        _stdout = launched.StandardOutput;
        _stderr = launched.StandardError;
        _log = log;
        _lease = lease;
        ActivationId = activationId;
        _onUnexpectedExit = onUnexpectedExit;
        _log.WriteLine(Line("SYS", $"process started pid={_process.Id}"));
        _stdoutPump = PumpAsync(_stdout, "OUT");
        _stderrPump = PumpAsync(_stderr, "ERR");
        ExitObserved = ObserveExitAsync();
    }

    public string? ActivationId { get; }
    public int ProcessId => _process.Id;
    public bool IsRunning => !_process.HasExited;
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;
    internal Task ExitObserved { get; }

    public static ManagedToolProcess Start(string name, string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, string logPath, IDisposable? lease = null,
        string? activationId = null, Action<DaemonExit>? onUnexpectedExit = null)
    {
        var job = new JobObject();
        var log = new RotatingFileWriter(logPath);
        try
        {
            var launched = NativeProcessLauncher.StartSuspendedInJob(executable, arguments, environment, job);
            return new ManagedToolProcess(name, job, launched, log, lease, activationId, onUnexpectedExit);
        }
        catch
        {
            job.Dispose();
            log.Dispose();
            lease?.Dispose();
            throw;
        }
    }

    private async Task ObserveExitAsync()
    {
        await _process.WaitForExitAsync();
        bool expected;
        lock (_lifecycle) expected = _stopRequested;
        _log.WriteLine(Line("SYS", $"process exited pid={_process.Id} code={_process.ExitCode}"));
        if (expected) return;
        // Allow already-buffered stderr to drain, without waiting for inherited child pipe handles forever.
        try { await _stderrPump.WaitAsync(TimeSpan.FromMilliseconds(500)); }
        catch (TimeoutException) { }
        _onUnexpectedExit?.Invoke(new DaemonExit(_name, _process.Id, _process.ExitCode, Volatile.Read(ref _lastError)));
    }

    public async Task StopAsync()
    {
        lock (_lifecycle)
        {
            if (!_jobClosed)
            {
                // A command issued after a spontaneous exit must not hide that exit.
                _stopRequested = !_process.HasExited;
                _log.WriteLine(Line("SYS", "stop requested"));
                _jobClosed = true;
                _job.Dispose();
            }
        }
        await ExitObserved.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(_stdoutPump, _stderrPump).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task PumpAsync(StreamReader reader, string stream)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (stream == "ERR" && !string.IsNullOrWhiteSpace(line))
                    Volatile.Write(ref _lastError, line.Length <= 2048 ? line : line[^2048..]);
                _log.WriteLine(Line(stream, line));
            }
        }
        catch (Exception exception) when (_jobClosed && exception is IOException or ObjectDisposedException) { }
    }

    private static string Line(string stream, string message)
        => $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {stream} {message}";

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await StopAsync(); }
        catch
        {
            // The Job Object is already closed. Continue releasing resources during host shutdown.
        }
        finally
        {
            _stdout.Dispose();
            _stderr.Dispose();
            _process.Dispose();
            _log.Dispose();
            _lease?.Dispose();
        }
    }
}
