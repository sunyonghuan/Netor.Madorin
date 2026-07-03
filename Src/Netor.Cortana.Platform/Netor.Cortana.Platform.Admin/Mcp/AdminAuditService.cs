using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Tables.Managers;

namespace Netor.Cortana.Platform.Admin.Mcp;

public sealed class AdminAuditService(PlatformDbContext dbContext, ILogger<AdminAuditService> logger)
{
    public async Task LogAsync(AdminAuditEntry entry, CancellationToken cancellationToken = default)
    {
        try
        {
            dbContext.ManagerAuditLogs.Add(new ManagerAuditLog
            {
                ManagerId = entry.ManagerId,
                TokenId = entry.TokenId,
                ToolName = entry.ToolName,
                SourceName = entry.Source,
                Ip = Truncate(entry.Ip, 64),
                Ua = Truncate(entry.Ua, 256),
                Success = entry.Success,
                ErrorCode = Truncate(entry.ErrorCode, 64),
                DurationMs = entry.DurationMs,
                CreatedUtc = DateTimeOffset.UtcNow
            });
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "写入管理员审计日志失败。ToolName={ToolName}, ManagerId={ManagerId}", entry.ToolName, entry.ManagerId);
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}

public sealed record AdminAuditEntry(
    string ManagerId,
    string? TokenId,
    string ToolName,
    string Source,
    string? Ip,
    string? Ua,
    bool Success,
    string? ErrorCode,
    int DurationMs);
