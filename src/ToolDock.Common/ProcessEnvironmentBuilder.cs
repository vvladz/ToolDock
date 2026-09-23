using System.Collections;

namespace ToolDock.Common;

public sealed record ResolvedProcessEnvironment(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<string> LogEntries);

public sealed class ProcessEnvironmentBuilder(ToolDockPaths paths)
{
    public ResolvedProcessEnvironment Build(
        IReadOnlyDictionary<string, EnvironmentValue> configured,
        IReadOnlyDictionary<string, string> catalogVariables)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                values[name] = value;
            }
        }

        var variables = new VariableStore(paths);
        var secrets = new SecretStore(paths);
        var catalogVariableLookup = catalogVariables.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        var resolved = new List<(string Name, string Value, string LogEntry)>();
        var missing = new List<(string Kind, string Name)>();

        foreach (var (name, source) in configured.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (source.Literal is not null)
            {
                resolved.Add((name, source.Literal, $"{name}: {source.Literal}"));
            }
            else if (source.Variable is not null)
            {
                if (catalogVariableLookup.TryGetValue(source.Variable, out var catalogValue))
                {
                    resolved.Add((name, catalogValue, $"{name}: {source.Variable} -> {catalogValue}"));
                }
                else if (variables.TryGet(source.Variable, out var value))
                {
                    resolved.Add((name, value, $"{name}: {source.Variable} -> {value}"));
                }
                else
                {
                    missing.Add(("variable", source.Variable));
                }
            }
            else if (source.Secret is not null)
            {
                if (secrets.TryGet(source.Secret, out var value))
                {
                    resolved.Add((name, value, $"{name}: {source.Secret} -> *******"));
                }
                else
                {
                    missing.Add(("secret", source.Secret));
                }
            }
        }

        if (missing.Count != 0)
        {
            var details = missing
                .Distinct()
                .OrderBy(item => item.Kind, StringComparer.Ordinal)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"missing {item.Kind}: {item.Name}; set with tdctl {item.Kind} set {item.Name}");
            throw new InvalidDataException(string.Join(Environment.NewLine, details));
        }

        foreach (var item in resolved)
        {
            values[item.Name] = item.Value;
        }

        return new ResolvedProcessEnvironment(
            values,
            resolved.Select(item => item.LogEntry).ToArray());
    }
}
