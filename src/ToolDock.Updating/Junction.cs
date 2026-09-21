using System.Diagnostics;

namespace ToolDock.Updating;

internal static class Junction
{
    public static async Task SwitchAsync(
        string junctionPath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(junctionPath)
            ?? throw new ArgumentException("Junction path has no parent directory.", nameof(junctionPath));
        Directory.CreateDirectory(parent);

        var temporary = Path.Combine(parent, $".current-{Guid.NewGuid():N}");
        var backup = Path.Combine(parent, $".previous-{Guid.NewGuid():N}");
        await CreateAsync(temporary, targetPath, cancellationToken);

        var movedExisting = false;
        try
        {
            if (Directory.Exists(junctionPath))
            {
                var attributes = File.GetAttributes(junctionPath);
                if (!attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException($"Refusing to replace non-junction directory: {junctionPath}");
                }

                Directory.Move(junctionPath, backup);
                movedExisting = true;
            }

            Directory.Move(temporary, junctionPath);

            if (movedExisting)
            {
                try
                {
                    Directory.Delete(backup);
                }
                catch (IOException)
                {
                    // The new junction is already active. A stale junction can be cleaned up later.
                }
            }
        }
        catch
        {
            if (!Directory.Exists(junctionPath) && movedExisting && Directory.Exists(backup))
            {
                Directory.Move(backup, junctionPath);
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary);
            }
        }
    }

    private static async Task CreateAsync(string junctionPath, string targetPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo)
            ?? throw new IOException("Failed to start cmd.exe while creating a directory junction.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new IOException(
                $"Failed to create junction {junctionPath}: {(await standardError).Trim()} {(await standardOutput).Trim()}".Trim());
        }

        await standardOutput;
        await standardError;
    }
}
