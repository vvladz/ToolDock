using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ToolDock.Common;

public sealed class VariableStore(ToolDockPaths paths)
{
    public void Set(string name, string value)
    {
        Validation.ValidateValueName(name);
        Validation.ValidateEnvironmentValue(value);
        using var configurationLock = ValueStoreLock.Acquire();
        var file = Read();
        SetCaseInsensitive(file.Variables, name, value);
        JsonFiles.WriteAtomicAsync(paths.VariablesFile, file).GetAwaiter().GetResult();
    }

    public bool Remove(string name)
    {
        Validation.ValidateValueName(name);
        using var configurationLock = ValueStoreLock.Acquire();
        var file = Read();
        var existing = FindName(file.Variables, name);
        if (existing is null)
        {
            return false;
        }

        file.Variables.Remove(existing);
        JsonFiles.WriteAtomicAsync(paths.VariablesFile, file).GetAwaiter().GetResult();
        return true;
    }

    public bool TryGet(string name, out string value)
    {
        Validation.ValidateValueName(name);
        var file = Read();
        var existing = FindName(file.Variables, name);
        if (existing is null)
        {
            value = string.Empty;
            return false;
        }

        value = file.Variables[existing];
        return true;
    }

    public IReadOnlyList<string> List()
        => Read().Variables.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private VariableStoreFile Read()
    {
        var file = JsonFiles.ReadOptionalAsync<VariableStoreFile>(paths.VariablesFile)
            .GetAwaiter().GetResult() ?? VariableStoreFile.Empty();
        if (file.Version != 1)
        {
            throw new InvalidDataException($"Unsupported variables file version: {file.Version}");
        }
        return file;
    }

    private static void SetCaseInsensitive(Dictionary<string, string> values, string name, string value)
    {
        var existing = FindName(values, name);
        if (existing is not null && !string.Equals(existing, name, StringComparison.Ordinal))
        {
            values.Remove(existing);
        }
        values[name] = value;
    }

    private static string? FindName(Dictionary<string, string> values, string name)
        => values.Keys.FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class SecretStore(ToolDockPaths paths)
{
    public void Set(string name, string value)
    {
        Validation.ValidateValueName(name);
        if (value.Length == 0)
        {
            throw new InvalidDataException("Secret value cannot be empty.");
        }
        Validation.ValidateEnvironmentValue(value);

        var plaintext = Encoding.UTF8.GetBytes(value);
        try
        {
            var ciphertext = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            try
            {
                using var configurationLock = ValueStoreLock.Acquire();
                var file = Read();
                var existing = FindName(file.Secrets, name);
                if (existing is not null && !string.Equals(existing, name, StringComparison.Ordinal))
                {
                    file.Secrets.Remove(existing);
                }
                file.Secrets[name] = Convert.ToBase64String(ciphertext);
                JsonFiles.WriteAtomicAsync(paths.SecretsFile, file).GetAwaiter().GetResult();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public bool Remove(string name)
    {
        Validation.ValidateValueName(name);
        using var configurationLock = ValueStoreLock.Acquire();
        var file = Read();
        var existing = FindName(file.Secrets, name);
        if (existing is null)
        {
            return false;
        }

        file.Secrets.Remove(existing);
        JsonFiles.WriteAtomicAsync(paths.SecretsFile, file).GetAwaiter().GetResult();
        return true;
    }

    public bool TryGet(string name, out string value)
    {
        Validation.ValidateValueName(name);
        var file = Read();
        var existing = FindName(file.Secrets, name);
        if (existing is null)
        {
            value = string.Empty;
            return false;
        }

        byte[] ciphertext;
        try
        {
            ciphertext = Convert.FromBase64String(file.Secrets[existing]);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"Secret is corrupted: {name}", exception);
        }

        try
        {
            var plaintext = ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
            try
            {
                value = Encoding.UTF8.GetString(plaintext);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException($"Secret cannot be decrypted for the current Windows user: {name}", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public IReadOnlyList<string> List()
        => Read().Secrets.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private SecretStoreFile Read()
    {
        var file = JsonFiles.ReadOptionalAsync<SecretStoreFile>(paths.SecretsFile)
            .GetAwaiter().GetResult() ?? SecretStoreFile.Empty();
        if (file.Version != 1)
        {
            throw new InvalidDataException($"Unsupported secrets file version: {file.Version}");
        }
        return file;
    }

    private static string? FindName(Dictionary<string, string> values, string name)
        => values.Keys.FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class VariableStoreFile
{
    public required int Version { get; init; }
    public required Dictionary<string, string> Variables { get; init; }

    public static VariableStoreFile Empty() => new() { Version = 1, Variables = [] };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class SecretStoreFile
{
    public required int Version { get; init; }
    public required Dictionary<string, string> Secrets { get; init; }

    public static SecretStoreFile Empty() => new() { Version = 1, Secrets = [] };
}

internal static class ValueStoreLock
{
    public static IDisposable Acquire()
    {
        var mutex = new Mutex(initiallyOwned: false, @"Local\ToolDock.Values");
        var ownsMutex = false;
        try
        {
            try
            {
                ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                ownsMutex = true;
            }

            if (!ownsMutex)
            {
                throw new TimeoutException("Timed out waiting for the ToolDock value store.");
            }

            return new MutexLease(mutex);
        }
        catch
        {
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
            }
            mutex.Dispose();
            throw;
        }
    }

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
