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
    public bool Autostart { get; init; }
    public bool Restart { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Executable { get; init; }
}

public sealed class InstalledState
{
    public Dictionary<string, InstalledTool> Tools { get; init; } = [];
}

public sealed class InstalledTool
{
    public required string Version { get; init; }
    public required string Path { get; init; }
}
