using System.Diagnostics;

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
