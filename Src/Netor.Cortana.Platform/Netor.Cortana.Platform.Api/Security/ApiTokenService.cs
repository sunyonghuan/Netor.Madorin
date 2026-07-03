using System.Security.Cryptography;
using System.Text;

namespace Netor.Cortana.Platform.Api.Security;

public sealed class ApiTokenService
{
    private readonly string _signingKey;

    public ApiTokenService(IConfiguration configuration)
        : this(configuration["ApiToken:SigningKey"])
    {
    }

    public ApiTokenService(string? signingKey)
    {
        _signingKey = string.IsNullOrWhiteSpace(signingKey)
            ? "development-only-cortana-platform-api-token-key"
            : signingKey;
    }

    public string CreateToken(string accountId, DateTimeOffset expiresAtUtc)
    {
        var payload = $"{accountId}|{expiresAtUtc.ToUnixTimeSeconds()}";
        var signature = Sign(payload);
        return $"{Base64UrlEncode(Encoding.UTF8.GetBytes(payload))}.{signature}";
    }

    public bool TryValidateToken(string token, out string accountId)
    {
        accountId = string.Empty;
        var parts = token.Split('.', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
        }
        catch (FormatException)
        {
            return false;
        }

        var expectedSignature = Encoding.UTF8.GetBytes(Sign(payload));
        var actualSignature = Encoding.UTF8.GetBytes(parts[1]);
        if (actualSignature.Length != expectedSignature.Length ||
            !CryptographicOperations.FixedTimeEquals(expectedSignature, actualSignature))
        {
            return false;
        }

        var payloadParts = payload.Split('|', 2);
        if (payloadParts.Length != 2 || !long.TryParse(payloadParts[1], out var expiresAtSeconds))
        {
            return false;
        }

        if (DateTimeOffset.FromUnixTimeSeconds(expiresAtSeconds) <= DateTimeOffset.UtcNow)
        {
            return false;
        }

        accountId = payloadParts[0];
        return !string.IsNullOrWhiteSpace(accountId);
    }

    private string Sign(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_signingKey));
        return Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        return Convert.FromBase64String(base64);
    }
}
