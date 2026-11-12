using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace ToolDock;

internal sealed class OutputPipe : IDisposable
{
    private readonly NamedPipeServerStream reader;
    private NamedPipeClientStream? writer;
    internal nint WriterHandle => writer!.SafePipeHandle.DangerousGetHandle();
    internal OutputPipe()
    {
        var name = "ToolDock.output." + Guid.NewGuid().ToString("N");
        reader = new(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            writer = new(".", name, PipeDirection.Out, PipeOptions.None, TokenImpersonationLevel.Anonymous, HandleInheritability.Inheritable);
            writer.Connect(5000);
            reader.WaitForConnection();
            Native.Check(Native.SetHandleInformation(WriterHandle, 1, 1));
        }
        catch { Dispose(); throw; }
    }
    internal void CloseWriter() { writer?.Dispose(); writer = null; }
    internal async Task PumpAsync(ILogger logger, string origin, CancellationToken token)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["Origin"] = origin });
        using var lines = new StreamReader(reader, System.Text.Encoding.UTF8, true, 4096, leaveOpen: true);
        try
        {
            while (await lines.ReadLineAsync(token) is { } line) logger.LogInformation("{Text:l}", line);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    public void Dispose() { CloseWriter(); reader.Dispose(); }
}
