using System.Security.Cryptography;
using System.Text.Json;

namespace TidalManager.Tidal;

public interface ISessionStore
{
    Task<TidalSession?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(TidalSession session, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed class ProtectedSessionStore(string filePath) : ISessionStore
{
    public async Task<TidalSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath)) return null;
        var protectedBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        try
        {
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<TidalSession>(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            // An unreadable session (other Windows user, corrupted file) just means signing in again.
            return null;
        }
    }

    public async Task SaveAsync(TidalSession session, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(session);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        var temporaryPath = filePath + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken);
            File.Move(temporaryPath, filePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(filePath)) File.Delete(filePath);
        return Task.CompletedTask;
    }
}
