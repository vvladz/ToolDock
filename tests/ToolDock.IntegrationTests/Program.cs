using System.Text.Json;
using ToolDock.Common.Logging;
using ToolDock.IntegrationTests;

if (args is ["--echo-arguments", .. var arguments])
{
    Console.WriteLine(JsonSerializer.Serialize(arguments));
    return;
}

if (args is ["--log-worker", var logPath, var prefix])
{
    using var writer = new RotatingFileWriter(logPath, 1024, 100);
    for (var i = 0; i < 100; i++) writer.WriteLine($"{prefix}:{i:D3}: concurrent rotation");
    return;
}
if (args is ["--exit", var error])
{
    if (error != "none") Console.Error.WriteLine(error);
    return;
}
if (args is ["--hold"])
{
    Console.WriteLine("child stdout");
    Console.Error.WriteLine("child stderr");
    await Task.Delay(2000);
    Environment.ExitCode = 23;
    return;
}
if (args is ["--sleep"])
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}
if (args is ["daemon-crash" or "tool-update-success" or "tool-update-failure"])
{
    Console.InputEncoding = new System.Text.UTF8Encoding(false);
    if (Environment.GetEnvironmentVariable("NOTIFY_MODE") == "no-input") await Task.Delay(Timeout.InfiniteTimeSpan);
    var text = await Console.In.ReadToEndAsync();
    var record = Environment.GetEnvironmentVariable("NOTIFY_RECORD");
    if (record is not null)
        await File.WriteAllTextAsync(record, JsonSerializer.Serialize(new
        {
            Type = args[0], Text = text, Value = Environment.GetEnvironmentVariable("NOTIFY_VALUE"), Pid = Environment.ProcessId
        }));
    if (Environment.GetEnvironmentVariable("NOTIFY_MODE") == "sleep") await Task.Delay(Timeout.InfiniteTimeSpan);
    if (Environment.GetEnvironmentVariable("NOTIFY_MODE") == "fail") Environment.ExitCode = 17;
    return;
}
await Suite.RunAsync(args.FirstOrDefault());
