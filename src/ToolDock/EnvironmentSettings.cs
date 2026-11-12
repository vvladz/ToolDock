using System.Text.Json;

namespace ToolDock;

internal static class EnvironmentSettings
{
    internal static void Apply(string root, Dictionary<string, JsonElement>? local, Dictionary<string, string> environment)
    {
        var effective = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        Merge(ReadGlobal(root), effective);
        Merge(local, effective);
        Dictionary<string, string>? secrets = null;
        foreach (var (name, entry) in effective)
        {
            string value;
            if (entry.ValueKind == JsonValueKind.String) value = entry.GetString()!;
            else
            {
                var secretName = entry.GetProperty("secret").GetString()!;
                secrets ??= new SecretStore(root).Read();
                if (!secrets.TryGetValue(secretName, out value!))
                    throw new InvalidDataException($"Secret '{secretName}' was not found.");
            }
            if (value.Contains('\0')) throw new InvalidDataException("Environment values cannot contain NUL characters.");
            environment[name] = value;
        }
    }

    private static Dictionary<string, JsonElement> ReadGlobal(string root)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(Path.Combine(root, "environment.json")), Settings.Json)
                ?? throw new InvalidDataException("environment.json must contain an object.");
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (JsonException) { throw new InvalidDataException("Invalid environment.json JSON."); }
    }

    private static void Merge(Dictionary<string, JsonElement>? source, Dictionary<string, JsonElement> destination)
    {
        if (source is null) throw new InvalidDataException("environment must be an object.");
        foreach (var (name, entry) in source)
        {
            if (name.Length == 0 || name.Contains('=') || name.Contains('\0'))
                throw new InvalidDataException("Invalid environment variable name.");
            if (entry.ValueKind == JsonValueKind.String)
            {
                if (entry.GetString()!.Contains('\0')) throw new InvalidDataException("Environment values cannot contain NUL characters.");
            }
            else if (entry.ValueKind == JsonValueKind.Object && entry.EnumerateObject().Count() == 1 &&
                entry.TryGetProperty("secret", out var secret) && secret.ValueKind == JsonValueKind.String)
            {
                SecretStore.ValidateName(secret.GetString()!);
            }
            else throw new InvalidDataException("Environment values must be strings or objects containing only a secret name.");
            destination[name] = entry;
        }
    }
}
