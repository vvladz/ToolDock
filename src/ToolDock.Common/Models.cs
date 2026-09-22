using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToolDock.Common;

public sealed class ToolDockConfig
{
    public required string CatalogUrl { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ToolCatalog
{
    public Dictionary<string, ToolDefinition> Tools { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ToolDefinition
{
    public required string Repo { get; init; }
    public required string Asset { get; init; }
    public bool Enabled { get; init; } = true;
    public required Dictionary<string, CommandDefinition> Commands { get; init; }
    public required Dictionary<string, DaemonDefinition> Daemons { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CommandDefinition
{
    public required string Executable { get; init; }
    public Dictionary<string, EnvironmentValue> Environment { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DaemonDefinition
{
    public required string Executable { get; init; }
    public string[] Arguments { get; init; } = [];
    public bool Autostart { get; init; }
    public bool RestartOnUpdate { get; init; }
    public Dictionary<string, EnvironmentValue> Environment { get; init; } = [];
}

[JsonConverter(typeof(EnvironmentValueJsonConverter))]
public sealed class EnvironmentValue
{
    private EnvironmentValue(string? literal, string? variable, string? secret)
    {
        Literal = literal;
        Variable = variable;
        Secret = secret;
    }

    public string? Literal { get; }
    public string? Variable { get; }
    public string? Secret { get; }

    public static EnvironmentValue FromLiteral(string value) => new(value, null, null);
    public static EnvironmentValue FromVariable(string name) => new(null, name, null);
    public static EnvironmentValue FromSecret(string name) => new(null, null, name);
}

internal sealed class EnvironmentValueJsonConverter : JsonConverter<EnvironmentValue>
{
    public override EnvironmentValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return EnvironmentValue.FromLiteral(reader.GetString()!);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Environment value must be a string or a variable/secret reference.");
        }

        string? kind = null;
        string? value = null;
        var ended = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Invalid environment reference.");
            }

            var property = reader.GetString()!;
            if (kind is not null ||
                property is not ("variable" or "secret"))
            {
                throw new JsonException($"Unknown or duplicate environment reference property: {property}");
            }

            kind = property;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"Environment {kind} reference must be a string.");
            }
            value = reader.GetString();
        }

        ended = reader.TokenType == JsonTokenType.EndObject;

        if (!ended || kind is null || value is null)
        {
            throw new JsonException("Environment reference must contain exactly one variable or secret property.");
        }

        return kind == "variable"
            ? EnvironmentValue.FromVariable(value)
            : EnvironmentValue.FromSecret(value);
    }

    public override void Write(Utf8JsonWriter writer, EnvironmentValue value, JsonSerializerOptions options)
    {
        if (value.Literal is not null)
        {
            writer.WriteStringValue(value.Literal);
            return;
        }

        writer.WriteStartObject();
        if (value.Variable is not null)
        {
            writer.WriteString("variable", value.Variable);
        }
        else if (value.Secret is not null)
        {
            writer.WriteString("secret", value.Secret);
        }
        else
        {
            throw new JsonException("Environment value has no source.");
        }
        writer.WriteEndObject();
    }
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

    public bool EntryPointsTracked { get; init; }
    public List<string> Commands { get; init; } = [];

    // Legacy state stored the path of the package's single executable.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }
}
