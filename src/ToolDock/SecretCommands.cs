using System.Text;

namespace ToolDock;

internal static class SecretCommands
{
    internal static async Task<int> ExecuteAsync(string[] args, string? root, CancellationToken cancellation)
    {
        var store = new SecretStore(root);
        if (args is ["secret", "list"])
        {
            foreach (var name in store.List()) Console.WriteLine(name);
            return 0;
        }
        if (args is ["secret", "delete", var deleted])
        {
            Console.WriteLine(store.Delete(deleted) ? "deleted" : "already absent");
            return 0;
        }
        if (args.Length is 3 or 4 && args[1] == "set" && (args.Length == 3 || args[3] == "--stdin"))
        {
            SecretStore.ValidateName(args[2]);
            var value = await ReadValueAsync(args.Length == 4, cancellation);
            cancellation.ThrowIfCancellationRequested();
            store.Set(args[2], value);
            Console.WriteLine("saved");
            return 0;
        }
        Console.Error.WriteLine("Usage: ToolDock.exe secret set <name> [--stdin] | secret list | secret delete <name>");
        return 2;
    }

    private static async Task<string> ReadValueAsync(bool stdin, CancellationToken cancellation)
    {
        if (stdin)
        {
            var value = await Console.In.ReadToEndAsync(cancellation);
            // Accept the line terminator added by shell pipelines, preserving other whitespace.
            return value.EndsWith("\r\n", StringComparison.Ordinal) ? value[..^2] : value.EndsWith('\n') ? value[..^1] : value;
        }
        if (Console.IsInputRedirected) throw new InvalidOperationException("Use --stdin when secret input is redirected.");
        Console.Error.Write("Secret value: ");
        var buffer = new StringBuilder();
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!Console.KeyAvailable) { await Task.Delay(25, cancellation); continue; }
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) return buffer.ToString();
                if (key.Key == ConsoleKey.Escape) throw new OperationCanceledException("Secret input cancelled.");
                if (key.Key == ConsoleKey.Backspace) { if (buffer.Length > 0) buffer.Length--; }
                else if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
            }
        }
        finally { buffer.Clear(); Console.Error.WriteLine(); }
    }
}
