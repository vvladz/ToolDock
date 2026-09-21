using System.IO.Pipes;
using System.Text;

namespace ToolDock.Common;

public sealed class StarterClient
{
    public const string PipeName = "ToolDock.Starter.v1";

    public async Task<string> SendAsync(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        await using var pipe = new NamedPipeClientStream(
            ".",
            PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeoutSource.Token);

        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await writer.WriteLineAsync(command.AsMemory(), timeoutSource.Token);
        return await reader.ReadLineAsync(timeoutSource.Token) ?? "ERROR starter closed the pipe without a response";
    }
}
