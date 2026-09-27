using Microsoft.Extensions.Logging;
using ToolDock.Common;
using ToolDock.Starter.Processes;

namespace ToolDock.Starter.Supervisor;

internal sealed class ToolSupervisor(ToolDockPaths paths, ILogger<ToolSupervisor> log, INotificationSender? notifications = null) : IAsyncDisposable
{
    private readonly Dictionary<string, ManagedToolProcess> _processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, Reconciliation>? _reconciliations;
    private readonly INotificationSender _notifications = notifications ?? new NotificationSender(paths, log);
    private readonly HashSet<Task> _notificationTasks = [];

    public async Task AutostartAsync(CancellationToken cancellationToken)
    {
        log.LogInformation("Loading autostart configuration");
        var catalog = (await InstalledSnapshot.ReadAsync(paths, cancellationToken)).Catalog;
        if (catalog.Tools.Count == 0)
        {
            log.LogWarning("Catalog cache is absent; waiting for updater");
            return;
        }

        Validation.ValidateCatalog(catalog);
        foreach (var (packageName, package) in catalog.Tools)
        {
            if (!package.Enabled)
            {
                continue;
            }

            foreach (var (daemonName, daemon) in package.Daemons)
            {
                if (!daemon.Autostart)
                {
                    continue;
                }

                try
                {
                    var response = await ExecuteAsync("start", daemonName, cancellationToken);
                    log.LogInformation("Autostart {Daemon}: {Response}", daemonName, response);
                }
                catch (Exception exception)
                {
                    log.LogError(exception, "Autostart failed for {Daemon}", daemonName);
                }
            }
        }
    }

    public async Task<string> ExecuteAsync(string command, string name, CancellationToken cancellationToken)
    {
        Validation.ValidateToolName(name);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (command == "stop")
            {
                var response = await StopCoreAsync(name);
                await LoadReconciliationsAsync(cancellationToken);
                foreach (var work in _reconciliations!.Values) work.Remaining.Remove(name);
                await SaveReconciliationsAsync(cancellationToken);
                return response;
            }
            return command switch
            {
                "start" => await StartCoreAsync(name, cancellationToken),
                "restart" => await RestartCoreAsync(name, cancellationToken),
                "status" => StatusCore(name),
                _ => $"ERROR unknown command: {command}"
            };
        }
        catch (Exception exception)
        {
            log.LogError(exception, "{Command} failed for {Daemon}", command, name);
            return $"ERROR {SingleLine(exception.Message)}";
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> PackageUpdatedAsync(
        string packageName,
        bool firstInstall,
        CancellationToken cancellationToken,
        string? activationId = null)
    {
        Validation.ValidateToolName(packageName);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await InstalledSnapshot.ReadAsync(paths, cancellationToken);
            var catalog = snapshot.Catalog;
            var package = Validation.FindDefinition(catalog, packageName);
            if (!package.Enabled)
            {
                return "OK package disabled";
            }

            if (activationId is not null && Validation.FindInstalled(snapshot.State, packageName).ActivationId != activationId)
                return "ERROR stale package activation";
            await LoadReconciliationsAsync(cancellationToken);
            if (activationId is null || !_reconciliations!.TryGetValue(packageName, out var work) || work.Id != activationId)
            {
                work = new Reconciliation(activationId, package.Daemons.Where(pair => firstInstall
                        ? pair.Value.Autostart
                        : pair.Value.RestartOnUpdate && _processes.TryGetValue(pair.Key, out var process) &&
                          process.IsRunning && (activationId is null || process.ActivationId != activationId))
                    .Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase));
                _reconciliations![packageName] = work;
                await SaveReconciliationsAsync(cancellationToken);
            }
            var changed = new List<string>();
            foreach (var daemonName in work.Remaining.ToArray())
            {
                // Keep failed restarts in the work list even if stopping the old process succeeded.
                // If the last receipt write failed, the process itself proves this activation was applied.
                var alreadyApplied = activationId is not null && _processes.TryGetValue(daemonName, out var process) &&
                    process.ActivationId == activationId;
                var response = alreadyApplied ? "OK already applied" : firstInstall
                    ? await StartCoreAsync(daemonName, cancellationToken) : await RestartCoreAsync(daemonName, cancellationToken);
                if (!response.StartsWith("OK", StringComparison.Ordinal)) return response;
                work.Remaining.Remove(daemonName);
                await SaveReconciliationsAsync(cancellationToken);
                changed.Add(daemonName);
            }

            return changed.Count == 0
                ? "OK no daemon changes"
                : $"OK {(firstInstall ? "started" : "restarted")}={string.Join(',', changed)}";
        }
        catch (Exception exception)
        {
            log.LogError(exception, "Package update reconciliation failed for {Package}", packageName);
            return $"ERROR {SingleLine(exception.Message)}";
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var catalog = (await InstalledSnapshot.ReadAsync(paths, cancellationToken)).Catalog;
            var names = catalog.Tools
                .SelectMany(pair => pair.Value.Daemons.Keys)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return names.Length == 0 ? "OK no daemons" : $"OK {string.Join(' ', names)}";
        }
        catch (Exception exception)
        {
            log.LogError(exception, "List failed");
            return $"ERROR {SingleLine(exception.Message)}";
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> StartCoreAsync(string name, CancellationToken cancellationToken)
    {
        if (_processes.TryGetValue(name, out var existing))
        {
            if (existing.IsRunning)
            {
                return $"OK running pid={existing.ProcessId}";
            }

            await existing.DisposeAsync();
            _processes.Remove(name);
        }

        var snapshot = await InstalledSnapshot.ReadAsync(paths, cancellationToken);
        var catalog = snapshot.Catalog;
        var (packageName, package, daemon) = Validation.FindDaemon(catalog, name);
        if (!package.Enabled)
        {
            return $"ERROR package is disabled: {packageName}";
        }

        var installed = Validation.FindInstalled(snapshot.State, packageName);
        var packageRoot = snapshot.PackageRoot(paths, packageName);
        var executable = Validation.ResolveExecutable(packageRoot, $"daemon {name}", daemon.Executable);
        if (!File.Exists(executable))
        {
            return $"ERROR installed executable is missing: {Path.GetRelativePath(paths.Root, executable)}";
        }

        var environment = new ProcessEnvironmentBuilder(paths).Build(daemon.Environment, installed.CatalogVariables ?? catalog.Variables);

        log.LogInformation(
            "Starting {Daemon} from {Package} {Version}",
            name,
            packageName,
            installed.Version);
        foreach (var entry in environment.LogEntries)
        {
            log.LogInformation("{EnvironmentEntry}", entry);
        }
        var process = ManagedToolProcess.Start(
            name,
            executable,
            daemon.Arguments,
            environment.Values,
            Path.Combine(paths.Logs, $"{name}.log"),
            VersionLease.Acquire(packageRoot), installed.ActivationId, NotifyExit);
        _processes.Add(name, process);
        return $"OK running pid={process.ProcessId}";
    }

    private async Task<string> StopCoreAsync(string name)
    {
        if (!_processes.Remove(name, out var process))
        {
            return "OK stopped";
        }

        log.LogInformation("Stopping {Daemon}", name);
        try
        {
            await process.StopAsync();
        }
        finally
        {
            await process.DisposeAsync();
        }
        return "OK stopped";
    }

    private async Task<string> RestartCoreAsync(string name, CancellationToken cancellationToken)
    {
        if (_processes.TryGetValue(name, out var process))
        {
            log.LogInformation("Restarting {Daemon}", name);
            _processes.Remove(name);
            try
            {
                await process.StopAsync();
            }
            finally
            {
                await process.DisposeAsync();
            }
        }

        return await StartCoreAsync(name, cancellationToken);
    }

    private string StatusCore(string name)
    {
        if (!_processes.TryGetValue(name, out var process))
        {
            return "OK stopped";
        }

        return process.IsRunning
            ? $"OK running pid={process.ProcessId}"
            : $"OK stopped exit={process.ExitCode}";
    }

    private void NotifyExit(DaemonExit exit)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                var message = $"Daemon exited unexpectedly: {exit.Name}\nPID: {exit.ProcessId}\nExit code: {exit.ExitCode}";
                if (exit.LastError is not null) message += $"\nStderr: {exit.LastError}";
                await _notifications.SendAsync("daemon-crash", message);
            }
            catch (Exception exception) { log.LogWarning(exception, "Daemon notification failed"); }
        });
        lock (_notificationTasks) _notificationTasks.Add(task);
        _ = task.ContinueWith(completed => { lock (_notificationTasks) _notificationTasks.Remove(completed); }, TaskScheduler.Default);
    }

    private static string SingleLine(string message)
        => message.Replace('\r', ' ').Replace('\n', ' ');

    private async Task LoadReconciliationsAsync(CancellationToken cancellationToken)
    {
        _reconciliations ??= new(await JsonFiles.ReadOptionalAsync<Dictionary<string, Reconciliation>>(
            Path.Combine(paths.State, "starter-reconciliations.json"), cancellationToken) ?? [], StringComparer.OrdinalIgnoreCase);
    }

    private Task SaveReconciliationsAsync(CancellationToken cancellationToken)
        => JsonFiles.WriteAtomicAsync(Path.Combine(paths.State, "starter-reconciliations.json"), _reconciliations, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var process in _processes.Values)
            {
                await process.DisposeAsync();
            }

            _processes.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
        Task[] pending;
        lock (_notificationTasks) pending = _notificationTasks.ToArray();
        await Task.WhenAll(pending);
    }

    private sealed record Reconciliation(string? Id, HashSet<string> Remaining);
}
