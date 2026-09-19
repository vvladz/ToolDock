using System.Text.Json;

namespace ToolDock.Common;

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<T> ReadRequiredAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException($"JSON file is empty: {path}");
    }

    public static async Task<T?> ReadOptionalAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        return await ReadRequiredAsync<T>(path, cancellationToken);
    }

    public static Task WriteAtomicAsync<T>(string path, T value, CancellationToken cancellationToken = default)
        => AtomicFile.WriteAsync(path, async stream =>
            await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken), cancellationToken);
}
