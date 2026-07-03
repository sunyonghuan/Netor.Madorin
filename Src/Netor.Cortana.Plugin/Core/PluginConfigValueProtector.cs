using System.Security.Cryptography;
using System.Text;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.Plugin;

/// <summary>
/// 使用本机用户数据目录中的随机密钥保护插件敏感配置。
/// </summary>
public sealed class PluginConfigValueProtector(IAppPaths appPaths) : IPluginConfigValueProtector
{
    private const string Prefix = "plugin-config-protected:v1:";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly Lock _keyLock = new();
    private byte[]? _key;

    /// <inheritdoc />
    public bool IsProtectedValue(string? value)
    {
        return value?.StartsWith(Prefix, StringComparison.Ordinal) == true;
    }

    /// <inheritdoc />
    public string Protect(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var plainBytes = Encoding.UTF8.GetBytes(value);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(GetOrCreateKey(), TagSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var payload = new byte[NonceSize + TagSize + cipherBytes.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, NonceSize);
        cipherBytes.CopyTo(payload, NonceSize + TagSize);

        return Prefix + Convert.ToBase64String(payload);
    }

    /// <inheritdoc />
    public string GetEffectiveValue(string storedValue)
    {
        ArgumentNullException.ThrowIfNull(storedValue);

        if (!IsProtectedValue(storedValue))
        {
            return storedValue;
        }

        var payloadText = storedValue[Prefix.Length..];
        var payload = DecodeBase64(payloadText, "插件配置密文格式无效。");
        if (payload.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("插件配置密文格式无效。");
        }

        var nonce = payload.AsSpan(0, NonceSize);
        var tag = payload.AsSpan(NonceSize, TagSize);
        var cipherBytes = payload.AsSpan(NonceSize + TagSize);
        var plainBytes = new byte[cipherBytes.Length];

        using var aes = new AesGcm(GetOrCreateKey(), TagSize);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    private byte[] GetOrCreateKey()
    {
        lock (_keyLock)
        {
            if (_key is not null)
            {
                return _key;
            }

            var keyPath = GetKeyPath();
            if (File.Exists(keyPath))
            {
                var storedKey = DecodeBase64(File.ReadAllText(keyPath).Trim(), "插件配置保护密钥格式无效。");
                if (storedKey.Length == KeySize)
                {
                    _key = storedKey;
                    return _key;
                }

                throw new CryptographicException("插件配置保护密钥格式无效。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            _key = RandomNumberGenerator.GetBytes(KeySize);
            File.WriteAllText(keyPath, Convert.ToBase64String(_key));
            TryMarkHidden(keyPath);
            return _key;
        }
    }

    private string GetKeyPath()
    {
        return Path.Combine(appPaths.UserDataDirectory, "security", "plugin-config.key");
    }

    private static byte[] DecodeBase64(string value, string errorMessage)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException(errorMessage, ex);
        }
    }

    private static void TryMarkHidden(string path)
    {
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}
