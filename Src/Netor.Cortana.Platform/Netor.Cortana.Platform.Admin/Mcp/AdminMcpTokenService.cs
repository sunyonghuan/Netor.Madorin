using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Tables.Managers;

namespace Netor.Cortana.Platform.Admin.Mcp;

public sealed class AdminMcpTokenService(PlatformDbContext dbContext)
{
    private const string TokenPrefix = "mcp_";
    private const int TokenBytesLength = 32;

    public async Task<IReadOnlyList<AdminMcpTokenSummary>> ListByManagerAsync(string managerId, CancellationToken cancellationToken = default)
    {
        var tokens = await dbContext.ManagerMcpTokens
            .AsNoTracking()
            .Where(x => x.ManagerId == managerId)
            .Select(x => new AdminMcpTokenSummary(
                x.ID,
                x.TokenPrefix,
                x.Note,
                x.Enabled,
                x.CreatedUtc,
                x.LastUsedUtc,
                x.LastUsedIp,
                x.LastUsedUa))
            .ToListAsync(cancellationToken);

        return tokens
            .OrderByDescending(x => x.CreatedUtc)
            .ToList();
    }

    public async Task<AdminMcpTokenResolveResult?> ResolveAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken) || !rawToken.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var tokenHash = HashToken(rawToken);
        var token = await dbContext.ManagerMcpTokens
            .AsNoTracking()
            .Include(x => x.Manager)
                .ThenInclude(x => x!.Role)
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);

        if (token is null || token.Manager is null)
        {
            return null;
        }

        var expected = Encoding.UTF8.GetBytes(token.TokenHash);
        var actual = Encoding.UTF8.GetBytes(tokenHash);
        if (expected.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            return null;
        }

        if (!token.Enabled || token.Manager.Status != 0)
        {
            return null;
        }

        return new AdminMcpTokenResolveResult(token, token.Manager);
    }

    public async Task<string> CreateAsync(string managerId, string? note, CancellationToken cancellationToken = default)
    {
        var rawToken = CreateRawToken();
        var entity = new ManagerMcpToken
        {
            ManagerId = managerId,
            TokenHash = HashToken(rawToken),
            TokenPrefix = rawToken[..Math.Min(12, rawToken.Length)],
            Note = NormalizeNote(note),
            Enabled = true,
            CreatedUtc = DateTimeOffset.UtcNow
        };

        dbContext.ManagerMcpTokens.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return rawToken;
    }

    public async Task<bool> RevokeAsync(string managerId, string tokenId, CancellationToken cancellationToken = default)
    {
        var token = await dbContext.ManagerMcpTokens
            .FirstOrDefaultAsync(x => x.ID == tokenId && x.ManagerId == managerId, cancellationToken);
        if (token is null)
        {
            return false;
        }

        dbContext.ManagerMcpTokens.Remove(token);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ToggleAsync(string managerId, string tokenId, bool enabled, CancellationToken cancellationToken = default)
    {
        var token = await dbContext.ManagerMcpTokens
            .FirstOrDefaultAsync(x => x.ID == tokenId && x.ManagerId == managerId, cancellationToken);
        if (token is null)
        {
            return false;
        }

        token.Enabled = enabled;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task TouchLastUsedAsync(string tokenId, string? ip, string? ua, CancellationToken cancellationToken = default)
    {
        var token = await dbContext.ManagerMcpTokens.FirstOrDefaultAsync(x => x.ID == tokenId, cancellationToken);
        if (token is null)
        {
            return;
        }

        token.LastUsedUtc = DateTimeOffset.UtcNow;
        token.LastUsedIp = Truncate(ip, 64);
        token.LastUsedUa = Truncate(ua, 256);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string CreateRawToken()
    {
        Span<byte> bytes = stackalloc byte[TokenBytesLength];
        RandomNumberGenerator.Fill(bytes);
        return TokenPrefix + Base64UrlEncode(bytes);
    }

    private static string HashToken(string rawToken)
        => Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string NormalizeNote(string? note)
        => Truncate(string.IsNullOrWhiteSpace(note) ? "未命名令牌" : note.Trim(), 64) ?? "未命名令牌";

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}

public sealed record AdminMcpTokenSummary(
    string Id,
    string Prefix,
    string Note,
    bool Enabled,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastUsedUtc,
    string? LastUsedIp,
    string? LastUsedUa);

public sealed record AdminMcpTokenResolveResult(ManagerMcpToken Token, Manager Manager);
