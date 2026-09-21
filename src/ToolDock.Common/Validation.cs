using System.Text.RegularExpressions;

namespace ToolDock.Common;

public static partial class Validation
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolNamePattern();

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryPattern();

    public static void ValidateCatalog(ToolCatalog catalog)
    {
        var packageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var commandNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var daemonNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, definition) in catalog.Tools)
        {
            ValidateToolName(name);
            if (!packageNames.Add(name))
            {
                throw new InvalidDataException($"Duplicate package name differs only by case: {name}");
            }

            if (!RepositoryPattern().IsMatch(definition.Repo) || definition.Repo.Contains("..", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Invalid GitHub repository for {name}: {definition.Repo}");
            }

            if (string.IsNullOrWhiteSpace(definition.Asset) ||
                definition.Asset.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                definition.Asset is "." or "..")
            {
                throw new InvalidDataException($"Invalid release asset name for {name}.");
            }

            if (definition.Commands.Count == 0 && definition.Daemons.Count == 0)
            {
                throw new InvalidDataException($"Package {name} has no commands or daemons.");
            }

            foreach (var (commandName, executable) in definition.Commands)
            {
                ValidateToolName(commandName);
                if (!commandNames.Add(commandName))
                {
                    throw new InvalidDataException($"Duplicate command name differs only by case: {commandName}");
                }

                _ = ValidateExecutablePath($"command {commandName}", executable);
            }

            foreach (var (daemonName, daemon) in definition.Daemons)
            {
                ValidateToolName(daemonName);
                if (!daemonNames.Add(daemonName))
                {
                    throw new InvalidDataException($"Duplicate daemon name differs only by case: {daemonName}");
                }

                _ = ValidateExecutablePath($"daemon {daemonName}", daemon.Executable);
                if (daemon.Arguments.Any(argument => argument is null || argument.Contains('\0')))
                {
                    throw new InvalidDataException($"Daemon {daemonName} has an invalid argument.");
                }
            }
        }
    }

    public static void ValidateToolName(string name)
    {
        if (!ToolNamePattern().IsMatch(name) || name is "." or "..")
        {
            throw new InvalidDataException($"Invalid tool name: {name}");
        }
    }

    public static string ValidateVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 100 || version is "." or ".." ||
            version.EndsWith('.') || version.EndsWith(' ') ||
            version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException($"Release tag cannot be used as a version directory: {version}");
        }

        return version;
    }

    public static string ValidateExecutablePath(string owner, string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidDataException($"Executable path for {owner} must be relative.");
        }

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var validationRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "ToolDockPathValidation");
        var full = Path.GetFullPath(normalized, validationRoot);
        var basePath = validationRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) ||
            normalized.IndexOfAny(['%', '!', '"', '^', '&', '|', '<', '>', '\r', '\n']) >= 0)
        {
            throw new InvalidDataException($"Unsafe executable path for {owner}.");
        }

        return normalized;
    }

    public static ToolDefinition FindDefinition(ToolCatalog catalog, string name)
        => catalog.Tools.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value
           ?? throw new KeyNotFoundException($"Unknown package: {name}");

    public static (string PackageName, ToolDefinition Package, DaemonDefinition Daemon) FindDaemon(
        ToolCatalog catalog,
        string name)
    {
        foreach (var (packageName, package) in catalog.Tools)
        {
            var daemon = package.Daemons
                .FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase));
            if (daemon.Value is not null)
            {
                return (packageName, package, daemon.Value);
            }
        }

        throw new KeyNotFoundException($"Unknown daemon: {name}");
    }

    public static InstalledTool FindInstalled(InstalledState state, string name)
        => state.Tools.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value
           ?? throw new KeyNotFoundException($"Package is not installed: {name}");
}
