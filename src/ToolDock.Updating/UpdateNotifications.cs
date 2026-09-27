using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using ToolDock.Common;

namespace ToolDock.Updating;

internal sealed class UpdateNotifications(ToolDockPaths paths, INotificationSender sender, ILogger log)
{
    public async Task FailureAsync(string? package, Exception error, string excerpt)
    {
        try
        {
            var failures = await ReadAsync();
            var key = package ?? "$run";
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(error.GetType().Name + ":" + error.Message)));
            if (failures.GetValueOrDefault(key) == fingerprint) return;
            var summary = error.Message.Replace('\r', ' ').Replace('\n', ' ');
            if (summary.Length > 500) summary = summary[..500] + "...";
            await SendAsync("tool-update-failure", $"Update failed: {package ?? "catalog/run"}\n{summary}\n\n{excerpt}");
            failures[key] = fingerprint;
            await JsonFiles.WriteAtomicAsync(paths.UpdateFailuresFile, failures);
        }
        catch (Exception exception) { log.LogWarning(exception, "Could not record notification failure state"); }
    }

    public async Task SucceededAsync(string? package)
    {
        try
        {
            var failures = await ReadAsync();
            if (failures.Remove(package ?? "$run")) await JsonFiles.WriteAtomicAsync(paths.UpdateFailuresFile, failures);
        }
        catch (Exception exception) { log.LogWarning(exception, "Could not clear notification failure state"); }
    }

    public async Task SendAsync(string type, string message)
    {
        try { await sender.SendAsync(type, message); }
        catch (Exception exception) { log.LogWarning(exception, "Notification delivery failed for {EventType}", type); }
    }

    private async Task<Dictionary<string, string>> ReadAsync()
        => new(await JsonFiles.ReadOptionalAsync<Dictionary<string, string>>(paths.UpdateFailuresFile) ?? [], StringComparer.OrdinalIgnoreCase);
}
