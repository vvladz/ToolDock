using System.Text;

namespace ToolDock.Common;

public static class AtomicFile
{
    public static async Task WriteTextAsync(
        string path,
        string contents,
        CancellationToken cancellationToken = default)
        => await WriteAsync(path, async stream =>
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteAsync(contents.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }, cancellationToken);

    public static async Task WriteAsync(
        string path,
        Func<FileStream, Task> write,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The target path has no directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await write(stream);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
