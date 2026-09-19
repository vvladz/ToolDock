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
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, definition) in catalog.Tools)
        {
            ValidateToolName(name);
            if (!names.Add(name))
            {
                throw new InvalidDataException($"Duplicate tool name differs only by case: {name}");
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

            _ = GetExecutablePath(name, definition);
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

    public static string GetExecutablePath(string name, ToolDefinition definition)
    {
        var relativePath = definition.Executable ?? $"{name}.exe";
        if (Path.IsPathRooted(relativePath) || string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidDataException($"Executable path for {name} must be relative.");
        }

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var validationRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "ToolDockPathValidation");
        var full = Path.GetFullPath(normalized, validationRoot);
        var basePath = validationRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) ||
            normalized.IndexOfAny(['%', '!', '"', '^', '&', '|', '<', '>', '\r', '\n']) >= 0)
        {
            throw new InvalidDataException($"Unsafe executable path for {name}.");
        }

        return normalized;
    }

    public static ToolDefinition FindDefinition(ToolCatalog catalog, string name)
        => catalog.Tools.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value
           ?? throw new KeyNotFoundException($"Unknown tool: {name}");

    public static InstalledTool FindInstalled(InstalledState state, string name)
        => state.Tools.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value
           ?? throw new KeyNotFoundException($"Tool is not installed: {name}");
}
