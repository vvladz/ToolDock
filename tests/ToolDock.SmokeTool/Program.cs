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
              "commands": {
                "legacy": "legacy.exe"
              },
              "daemons": {}
            }
          }
        }
        """;
    RequireRejected(legacyCatalog);

    const string ambiguousEnvironment = """
        {
          "tools": {
            "invalid": {
              "repo": "example/invalid",
              "asset": "invalid.zip",
              "commands": {
                "invalid": {
                  "executable": "invalid.exe",
                  "environment": {
                    "TOKEN": {
                      "variable": "token.value",
                      "secret": "token.secret"
                    }
                  }
                }
              },
              "daemons": {}
            }
          }
        }
        """;
    RequireRejected(ambiguousEnvironment);

    const string validCatalog = """
        {
          "tools": {
            "valid": {
              "repo": "example/valid",
              "asset": "valid.zip",
              "commands": {
                "valid": {
                  "executable": "valid.exe",
                  "environment": {
                    "MODE": "test",
                    "SERVER": { "variable": "service.server" },
                    "TOKEN": { "secret": "service.token" }
                  }
                }
              },
              "daemons": {}
            }
          }
        }
        """;
    var catalog = JsonSerializer.Deserialize<ToolCatalog>(validCatalog, JsonFiles.Options)
        ?? throw new InvalidOperationException("Valid catalog was empty.");
    Validation.ValidateCatalog(catalog);
    var roundTrip = JsonSerializer.Deserialize<ToolCatalog>(
        JsonSerializer.Serialize(catalog, JsonFiles.Options),
        JsonFiles.Options) ?? throw new InvalidOperationException("Catalog round trip was empty.");
    Validation.ValidateCatalog(roundTrip);

    Console.WriteLine("catalog schema verified");
    return;
}

if (args is ["--command", .. var commandArguments])
{
    Console.WriteLine($"command args={string.Join('|', commandArguments)}");
    Console.WriteLine($"command environment={(EnvironmentIsExpected() ? "ok" : "invalid")}");
    Console.Error.WriteLine("command stderr ready");
    Environment.ExitCode = 23;
    return;
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
Console.WriteLine($"environment={(EnvironmentIsExpected() ? "ok" : "invalid")}");
Console.WriteLine($"child pid={child.Id}");
Console.Error.WriteLine("stderr ready");
await Task.Delay(Timeout.InfiniteTimeSpan);

static bool EnvironmentIsExpected()
    => Environment.GetEnvironmentVariable("LITERAL_VALUE") == "literal-value" &&
       Environment.GetEnvironmentVariable("VARIABLE_VALUE") == "variable-value" &&
       Environment.GetEnvironmentVariable("SECRET_VALUE") == "secret-value";

static void RequireRejected(string json)
{
    try
    {
        _ = JsonSerializer.Deserialize<ToolCatalog>(json, JsonFiles.Options);
    }
    catch (JsonException)
    {
        return;
    }

    throw new InvalidOperationException("Invalid catalog was accepted.");
}
