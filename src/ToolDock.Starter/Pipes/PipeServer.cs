using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;
using ToolDock.Common;
using ToolDock.Starter.Supervisor;

namespace ToolDock.Starter.Pipes;

internal sealed class PipeServer(ToolSupervisor supervisor, ILogger<PipeServer> log)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        log.LogInformation("Pipe server started: {PipeName}", StarterClient.PipeName);
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = CurrentUserPipe.CreateServer(StarterClient.PipeName);

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                await HandleAsync(pipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                log.LogError(exception, "Pipe request failed");
            }
        }
    }

    private async Task HandleAsync(Stream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        var request = await reader.ReadLineAsync(requestTimeout.Token);
        var response = await DispatchAsync(request, cancellationToken);
        await writer.WriteLineAsync(response.AsMemory(), cancellationToken);
    }

    private Task<string> DispatchAsync(string? request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request))
        {
            return Task.FromResult("ERROR empty request");
        }

        var parts = request.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts is ["list"])
        {
            return supervisor.ListAsync(cancellationToken);
        }
        if (parts is [var command, var name])
        {
            return supervisor.ExecuteAsync(command.ToLowerInvariant(), name, cancellationToken);
        }

        return Task.FromResult("ERROR expected: list or start|stop|restart|status <tool>");
    }
}
