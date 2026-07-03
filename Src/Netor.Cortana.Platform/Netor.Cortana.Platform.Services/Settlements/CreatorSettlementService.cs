using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Assets;
using Netor.Cortana.Platform.Entitys.Tables.Orders;
using Netor.Cortana.Platform.Entitys.Tables.Settlements;

namespace Netor.Cortana.Platform.Services.Settlements;

/// <summary>
/// 创作者收益结算服务。
/// </summary>
public sealed class CreatorSettlementService(PlatformDbContext dbContext)
{
    private const decimal PlatformFeeRate = 0.30m;

    public async Task<CreatorSettlement?> CreatePendingSettlementAsync(
        Order order,
        Asset asset,
        string currency,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(asset.OwnerAccountId) || order.Money <= 0)
        {
            return null;
        }

        var exists = await dbContext.CreatorSettlements.AnyAsync(x => x.OrderId == order.ID, cancellationToken);
        if (exists)
        {
            return null;
        }

        var platformFee = Math.Round(order.Money * PlatformFeeRate, 2, MidpointRounding.AwayFromZero);
        var settlement = dbContext.CreatorSettlements.Add(new CreatorSettlement
        {
            CreatorAccountId = asset.OwnerAccountId,
            AssetId = asset.ID,
            OrderId = order.ID,
            GrossAmount = order.Money,
            PlatformFeeAmount = platformFee,
            NetAmount = order.Money - platformFee,
            Currency = currency,
            Status = SettlementStatus.Pending,
            Notes = "支付成功后自动生成待结算收益。"
        }).Entity;

        return settlement;
    }

    public async Task<bool> MarkSettledAsync(string settlementId, string notes, CancellationToken cancellationToken = default)
    {
        var settlement = await dbContext.CreatorSettlements.FirstOrDefaultAsync(x => x.ID == settlementId, cancellationToken);
        if (settlement is null || settlement.Status != SettlementStatus.Pending)
        {
            return false;
        }

        settlement.Status = SettlementStatus.Settled;
        settlement.Notes = notes.Trim();
        settlement.SettledAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> FreezeAsync(string settlementId, string notes, CancellationToken cancellationToken = default)
    {
        var settlement = await dbContext.CreatorSettlements.FirstOrDefaultAsync(x => x.ID == settlementId, cancellationToken);
        if (settlement is null || settlement.Status == SettlementStatus.Settled)
        {
            return false;
        }

        settlement.Status = SettlementStatus.Frozen;
        settlement.Notes = notes.Trim();
        settlement.SettledAtUtc = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RejectAsync(string settlementId, string notes, CancellationToken cancellationToken = default)
    {
        var settlement = await dbContext.CreatorSettlements.FirstOrDefaultAsync(x => x.ID == settlementId, cancellationToken);
        if (settlement is null || settlement.Status == SettlementStatus.Settled)
        {
            return false;
        }

        settlement.Status = SettlementStatus.Rejected;
        settlement.Notes = notes.Trim();
        settlement.SettledAtUtc = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
