using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Admin.Operations;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Tables.Managers;

namespace Netor.Cortana.Platform.Admin.Mcp;

public sealed class AdminMcpIdempotencyService(PlatformDbContext dbContext)
{
    public async Task<OperationResult<T>?> TryReplayAsync<T>(
        string managerId,
        string toolName,
        string? requestId,
        JsonTypeInfo<OperationResult<T>> resultJsonTypeInfo,
        CancellationToken cancellationToken = default)
    {
        var hash = CreateHashOrNull(managerId, toolName, requestId);
        if (hash is null)
        {
            return null;
        }

        var record = await dbContext.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Hash == hash, cancellationToken);
        if (record is null)
        {
            return null;
        }

        var result = JsonSerializer.Deserialize(record.ResultJson, resultJsonTypeInfo);
        if (result is null)
        {
            return OperationResult<T>.Fail(OperationErrorCodes.Internal, "幂等记录反序列化失败。");
        }

        return result with
        {
            ErrorCode = result.Success ? OperationErrorCodes.IdempotentReplay : result.ErrorCode,
            Message = string.IsNullOrWhiteSpace(result.Message) ? "命中幂等记录，已回放上次结果。" : result.Message
        };
    }

    public async Task RecordAsync<T>(
        string managerId,
        string toolName,
        string? requestId,
        OperationResult<T> result,
        JsonTypeInfo<OperationResult<T>> resultJsonTypeInfo,
        CancellationToken cancellationToken = default)
    {
        var hash = CreateHashOrNull(managerId, toolName, requestId);
        if (hash is null)
        {
            return;
        }

        dbContext.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Hash = hash,
            ToolName = toolName,
            ManagerId = managerId,
            RequestId = requestId!.Trim(),
            ResultJson = JsonSerializer.Serialize(result, resultJsonTypeInfo),
            CreatedUtc = DateTimeOffset.UtcNow
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
        }
    }

    private static string? CreateHashOrNull(string managerId, string toolName, string? requestId)
    {
        if (string.IsNullOrWhiteSpace(managerId) ||
            string.IsNullOrWhiteSpace(toolName) ||
            string.IsNullOrWhiteSpace(requestId))
        {
            return null;
        }

        var payload = $"{managerId.Trim()}:{toolName.Trim()}:{requestId.Trim()}";
        return Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
