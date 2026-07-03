using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.Versioning;
using Netor.Cortana.Entitys;
using Netor.Cortana.Store.Abstractions;
using Netor.Cortana.Store.Models;

namespace Netor.Cortana.Store.Services;

[SupportedOSPlatform("windows")]
public sealed class PlatformAccountStore(IAppPaths appPaths) : IPlatformAccountStore
{
    private string SessionPath => Path.Combine(appPaths.UserDataDirectory, "store", "session.bin");

    public async Task<PlatformAccountSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SessionPath))
        {
            return null;
        }

        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(SessionPath, cancellationToken);
            var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize(bytes, StoreJsonContext.Default.PlatformAccountSession);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task SaveAsync(PlatformAccountSession session, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(session, StoreJsonContext.Default.PlatformAccountSession);
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(SessionPath, protectedBytes, cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(SessionPath))
        {
            File.Delete(SessionPath);
        }

        return Task.CompletedTask;
    }
}
