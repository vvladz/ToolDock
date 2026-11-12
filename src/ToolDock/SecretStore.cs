using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace ToolDock;

internal sealed class SecretStore(string? root = null)
{
    private readonly string directory = Paths.ResolveRoot(root);
    private string StoreFile => Path.Combine(directory, "secrets.dat");

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl))
            throw new ArgumentException("Secret names must contain 1-256 characters and no control characters.");
    }

    internal Dictionary<string, string> Read()
    {
        byte[] encrypted;
        try
        {
            using var file = new FileStream(StoreFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var contents = new MemoryStream();
            file.CopyTo(contents);
            encrypted = contents.ToArray();
        }
        catch (FileNotFoundException) { return new(StringComparer.OrdinalIgnoreCase); }
        catch (DirectoryNotFoundException) { return new(StringComparer.OrdinalIgnoreCase); }

        var plaintext = Dpapi.Transform(encrypted, protect: false);
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext)
                ?? throw new InvalidDataException("Invalid secret store.");
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in stored)
            {
                ValidateName(name);
                if (value is null || value.Contains('\0') || !result.TryAdd(name, value))
                    throw new InvalidDataException("Invalid secret store.");
            }
            return result;
        }
        catch (JsonException) { throw new InvalidDataException("Invalid secret store."); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    internal string[] List() => Read().Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();

    internal void Set(string name, string value)
    {
        ValidateName(name);
        if (value.Contains('\0')) throw new ArgumentException("Secret values cannot contain NUL characters.");
        using var heldLock = Lock();
        var secrets = Read();
        secrets[name] = value;
        Write(secrets);
    }

    internal bool Delete(string name)
    {
        ValidateName(name);
        using var heldLock = Lock();
        var secrets = Read();
        if (!secrets.Remove(name)) return false;
        Write(secrets);
        return true;
    }

    private FileStream Lock()
    {
        Directory.CreateDirectory(directory);
        try { return new FileStream(Path.Combine(directory, "secrets.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) == 32)
        { throw new IOException("Secret store is in use by another command."); }
    }

    private void Write(Dictionary<string, string> secrets)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(secrets);
        byte[] encrypted;
        try { encrypted = Dpapi.Transform(plaintext, protect: true); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        var temporary = StoreFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(encrypted); file.Flush(flushToDisk: true); }
            File.Move(temporary, StoreFile, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal static class Dpapi
{
    internal static byte[] Transform(byte[] data, bool protect)
    {
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        var input = new Blob { Length = data.Length, Data = pinned.AddrOfPinnedObject() };
        var output = new Blob();
        try
        {
            // Current-user scope: deliberately omit CRYPTPROTECT_LOCAL_MACHINE.
            const uint uiForbidden = 1;
            var success = protect
                ? CryptProtectData(ref input, null, 0, 0, 0, uiForbidden, out output)
                : CryptUnprotectData(ref input, 0, 0, 0, 0, uiForbidden, out output);
            if (!success) throw new CryptographicException(protect
                ? "Cannot encrypt the secret store for the current Windows user."
                : "Cannot decrypt the secret store for the current Windows user.");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            pinned.Free();
            if (output.Data != 0)
            {
                if (!protect && output.Length > 0) Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length);
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public nint Data; }
    [DllImport("crypt32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob input, string? description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32")] private static extern nint LocalFree(nint memory);
}
