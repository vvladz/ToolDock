using System.Text;

namespace ToolDock.Common.Logging;

public sealed class RotatingFileWriter : IDisposable
{
    public const long DefaultMaxBytes = 10 * 1024 * 1024;
    public const int DefaultArchiveCount = 5;

    private readonly object _sync = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _archiveCount;
    private StreamWriter? _writer;
    private bool _disposed;

    public RotatingFileWriter(
        string path,
        long maxBytes = DefaultMaxBytes,
        int archiveCount = DefaultArchiveCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(archiveCount);

        _path = path;
        _maxBytes = maxBytes;
        _archiveCount = archiveCount;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public void WriteLine(string line)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var bytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            EnsureWriter(bytes);
            _writer!.WriteLine(line);
            _writer.Flush();
        }
    }

    private void EnsureWriter(int incomingBytes)
    {
        _writer ??= OpenWriter();
        if (_writer.BaseStream.Length == 0 || _writer.BaseStream.Length + incomingBytes <= _maxBytes)
        {
            return;
        }

        _writer.Dispose();
        _writer = null;
        Rotate();
        _writer = OpenWriter();
    }

    private StreamWriter OpenWriter()
        => new(new FileStream(
            _path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete), new UTF8Encoding(false))
        {
            AutoFlush = true
        };

    private void Rotate()
    {
        if (_archiveCount == 0)
        {
            File.Delete(_path);
            return;
        }

        File.Delete($"{_path}.{_archiveCount}");
        for (var index = _archiveCount - 1; index >= 1; index--)
        {
            var source = $"{_path}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{_path}.{index + 1}");
            }
        }

        if (File.Exists(_path))
        {
            File.Move(_path, $"{_path}.1");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
