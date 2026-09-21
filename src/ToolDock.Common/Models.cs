using System.Text.Json.Serialization;

namespace ToolDock.Common;

public sealed class ToolDockConfig
{
    public required string CatalogUrl { get; init; }
}

public sealed class ToolCatalog
{
    public Dictionary<string, ToolDefinition> Tools { get; init; } = [];
}

public sealed class ToolDefinition
{
    public required string Repo { get; init; }
    public required string Asset { get; init; }
    public bool Enabled { get; init; } = true;
    public Dictionary<string, string> Commands { get; init; } = [];
    public Dictionary<string, DaemonDefinition> Daemons { get; init; } = [];

    // Legacy single-entry-point catalog fields. New catalogs should use Commands and Daemons.
    public bool Autostart { get; init; }
    public bool Restart { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Executable { get; init; }
}

public sealed class DaemonDefinition
{
    public required string Executable { get; init; }
    public string[] Arguments { get; init; } = [];
    public bool Autostart { get; init; }
    public bool RestartOnUpdate { get; init; }
}

public sealed class InstalledState
{
    public Dictionary<string, InstalledTool> Tools { get; init; } = [];
}

public sealed class InstalledTool
{
    public required string Version { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Root { get; init; }

    public List<string> Commands { get; init; } = [];

    // Legacy state stored the path of the package's single executable.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }
}
