using ToolDock.Common;

namespace ToolDock.Updating;

internal static class CommandShims
{
    private static string Contents(string name)
        => $"@echo off\r\n\"%~dp0ToolDock.Client.exe\" exec {name} -- %*\r\nexit /b %ERRORLEVEL%\r\n";

    public static void Check(ToolDockPaths paths, string name)
    {
        Validation.ValidateCommandName(name);
        var path = Path.Combine(paths.Bin, $"{name}.cmd");
        if (File.Exists(path) && !IsOwned(paths, name))
        {
            throw new IOException($"Refusing to overwrite a user command: {path}");
        }
    }

    public static bool IsOwned(ToolDockPaths paths, string name)
        => File.Exists(Path.Combine(paths.Bin, $"{name}.cmd")) &&
           File.ReadAllText(Path.Combine(paths.Bin, $"{name}.cmd")) == Contents(name);

    public static async Task WriteAsync(ToolDockPaths paths, string name, CancellationToken cancellationToken)
    {
        Check(paths, name);
        if (!IsOwned(paths, name))
        {
            await AtomicFile.WriteTextAsync(Path.Combine(paths.Bin, $"{name}.cmd"), Contents(name), cancellationToken);
        }
    }
}
