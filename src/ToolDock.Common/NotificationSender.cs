using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ToolDock.Common;

public interface INotificationSender
{
    Task SendAsync(string eventType, string message, CancellationToken cancellationToken = default);
}

public sealed class NotificationSender(ToolDockPaths paths, ILogger log) : INotificationSender
{
    public async Task SendAsync(string eventType, string message, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await JsonFiles.ReadOptionalAsync<ToolDockConfig>(paths.ConfigFile, cancellationToken);
            if (string.IsNullOrWhiteSpace(config?.NotificationCommand)) return;
            if (config.NotificationTimeoutSeconds is < 1 or > 60)
                throw new InvalidDataException("Notification timeout must be between 1 and 60 seconds.");
            var snapshot = await InstalledSnapshot.ReadAsync(paths, cancellationToken);
            var (name, package, command) = Validation.FindCommand(snapshot.Catalog, config.NotificationCommand);
            if (!package.Enabled) throw new InvalidOperationException($"Notification package is disabled: {name}");
            var installed = Validation.FindInstalled(snapshot.State, name);
            var root = snapshot.PackageRoot(paths, name);
            using var lease = VersionLease.Acquire(root);
            var environment = new ProcessEnvironmentBuilder(paths).Build(command.Environment,
                installed.CatalogVariables ?? snapshot.Catalog.Variables);
            var info = new ProcessStartInfo(Validation.ResolveExecutable(root, name, command.Executable))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                WorkingDirectory = root
            };
            info.ArgumentList.Add(eventType);
            info.Environment.Clear();
            foreach (var (key, value) in environment.Values) info.Environment[key] = value;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(config.NotificationTimeoutSeconds));
            using var process = Process.Start(info) ?? throw new IOException("Could not start notification handler.");
            // Discard output incrementally: a noisy handler cannot fill a pipe or grow a string without bound.
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            try
            {
                await process.StandardInput.WriteAsync(message.AsMemory(), timeout.Token);
                await process.StandardInput.FlushAsync(timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(stdout, stderr);
                if (process.ExitCode != 0) throw new IOException($"Notification handler exited with code {process.ExitCode}.");
            }
            finally
            {
                timeout.Cancel();
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                try { await Task.WhenAll(stdout, stderr); }
                catch (Exception exception) when (exception is OperationCanceledException or IOException) { }
            }
        }
        catch (Exception exception)
        {
            // Delivery is best effort and cannot change a daemon or package outcome, or emit another event.
            log.LogWarning(exception, "Notification delivery failed for {EventType}", eventType);
        }
    }
}
