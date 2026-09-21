using System.Diagnostics;
using System.Text.Json;
using ToolDock.Common;

if (args is ["--verify-catalog-schema"])
{
    const string legacyCatalog = """
        {
          "tools": {
            "legacy": {
              "repo": "example/legacy",
              "asset": "legacy.zip",
              "executable": "legacy.exe",
              "autostart": true,
              "restart": true
            }
          }
        }
        """;
    try
    {
        _ = JsonSerializer.Deserialize<ToolCatalog>(legacyCatalog, JsonFiles.Options);
    }
    catch (JsonException)
    {
        Console.WriteLine("legacy catalog rejected");
        return;
    }

    throw new InvalidOperationException("Legacy catalog was accepted.");
}

if (args is ["--child"])
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}

using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--child")
{
    UseShellExecute = false
}) ?? throw new InvalidOperationException("Could not start smoke-test child process.");

Console.WriteLine($"args={string.Join('|', args)}");
Console.WriteLine($"child pid={child.Id}");
Console.Error.WriteLine("stderr ready");
await Task.Delay(Timeout.InfiniteTimeSpan);
