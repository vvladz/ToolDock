using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ToolDock.Updating;

internal static class Junction
{
    public static async Task SwitchAsync(
        string junctionPath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(junctionPath)
            ?? throw new ArgumentException("Junction path has no parent directory.", nameof(junctionPath));
        Directory.CreateDirectory(parent);

        var temporary = Path.Combine(parent, $".current-{Guid.NewGuid():N}");
        var backup = Path.Combine(parent, $".previous-{Guid.NewGuid():N}");
        await CreateAsync(temporary, targetPath, cancellationToken);

        var movedExisting = false;
        try
        {
            if (Directory.Exists(junctionPath))
            {
                var attributes = File.GetAttributes(junctionPath);
                if (!attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new IOException($"Refusing to replace non-junction directory: {junctionPath}");
                }

                Directory.Move(junctionPath, backup);
                movedExisting = true;
            }

            Directory.Move(temporary, junctionPath);

            if (movedExisting)
            {
                try
                {
                    Directory.Delete(backup);
                }
                catch (IOException)
                {
                    // The new junction is already active. A stale junction can be cleaned up later.
                }
            }
        }
        catch
        {
            if (!Directory.Exists(junctionPath) && movedExisting && Directory.Exists(backup))
            {
                Directory.Move(backup, junctionPath);
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary);
            }
        }
    }

    private static Task CreateAsync(string junctionPath, string targetPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = Path.GetFullPath(targetPath);
        var substitute = Encoding.Unicode.GetBytes(target.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\??\UNC\" + target[2..] : @"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        // REPARSE_DATA_BUFFER: header, mount-point offsets, two NUL-terminated UTF-16 paths.
        var buffer = new byte[20 + substitute.Length + print.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003); // IO_REPARSE_TAG_MOUNT_POINT
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)print.Length));
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 18 + substitute.Length);
        Directory.CreateDirectory(junctionPath);
        try
        {
            using var handle = CreateFile(junctionPath, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length,
                    IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot create junction: {junctionPath}");
            }
        }
        catch
        {
            Directory.Delete(junctionPath);
            throw;
        }
        return Task.CompletedTask;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
