using System.Text;

namespace ToolDock;

internal static class CmdCommand
{
    internal static string Build(string script, IEnumerable<string> arguments)
    {
        // cmd.exe does not accept the verbatim paths returned by FinalPath.
        if (script.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) script = @"\\" + script[8..];
        else if (script.StartsWith(@"\\?\", StringComparison.Ordinal)) script = script[4..];
        if (script.IndexOfAny(['"', '%', '^', '\r', '\n', '\0']) >= 0)
            throw new InvalidDataException("CMD launcher paths cannot contain quotes, percent signs, carets, or line breaks.");

        var command = new StringBuilder("cmd.exe /d /e:on /v:off /s /c \"");
        command.Append('"').Append(script).Append('"');
        foreach (var argument in arguments)
        {
            if (argument.IndexOfAny(['\r', '\n', '\0']) >= 0)
                throw new InvalidDataException("CMD arguments cannot contain line breaks or NUL characters.");
            command.Append(" \"");
            var slashes = 0;
            foreach (var character in argument)
            {
                if (character == '\\') { command.Append(character); slashes++; continue; }
                if (character == '"') command.Append('\\', slashes).Append('"');
                // An empty substring expansion prevents a following %NAME% from expanding.
                // Command extensions must be on; delayed expansion must be off.
                if (character == '%') command.Append("%%cd:~,");
                command.Append(character);
                slashes = 0;
            }
            command.Append('\\', slashes).Append('"');
        }
        command.Append('"');
        if (command.Length > 8191) throw new InvalidDataException("CMD command line exceeds 8191 characters.");
        return command.ToString();
    }
}
