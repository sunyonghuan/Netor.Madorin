using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Services.Packages;
using Netor.Cortana.Platform.Services.Risk;

namespace Netor.Cortana.Platform.Services.Reviews;

/// <summary>
/// 资源审核状态流转服务。
/// </summary>
public sealed class AssetReviewService(
    PlatformDbContext dbContext,
    PackageRiskService packageRiskService,
    PackageStorageService packageStorageService)
{
    public async Task<bool> ApproveAsync(string reviewId, string reviewerId, string notes, CancellationToken cancellationToken = default)
    {
        var review = await dbContext.AssetReviews
            .Include(x => x.Asset)
            .Include(x => x.AssetVersion)
            .FirstOrDefaultAsync(x => x.ID == reviewId && x.Status == AssetReviewStatus.Pending, cancellationToken);
        if (review?.Asset is null || review.AssetVersion is null)
        {
            return false;
        }

        var risk = packageRiskService.AssessMetadata(review.AssetVersion);
        if (!risk.Passed)
        {
            RejectForRisk(review, reviewerId, risk.Issues);
            await dbContext.SaveChangesAsync(cancellationToken);
            return false;
        }

        var integrity = await packageStorageService.VerifyPackageAsync(review.AssetVersion, cancellationToken);
        if (integrity.Status != PackageIntegrityStatus.Verified)
        {
            RejectForRisk(review, reviewerId, [GetPackageIntegrityIssue(integrity.Status)]);
            await dbContext.SaveChangesAsync(cancellationToken);
            return false;
        }

        PackageRiskAssessment archiveRisk;
        try
        {
            using var packageStream = packageStorageService.OpenPackageRead(review.AssetVersion);
            archiveRisk = packageRiskService.AssessPackageArchive(packageStream, review.Asset.Type);
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
        {
            archiveRisk = new PackageRiskAssessment(false, ["资源包文件不存在或路径非法"]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            archiveRisk = new PackageRiskAssessment(false, ["资源包存储服务不可用或权限异常"]);
        }

        if (!archiveRisk.Passed)
        {
            RejectForRisk(review, reviewerId, archiveRisk.Issues);
            await dbContext.SaveChangesAsync(cancellationToken);
            return false;
        }

        review.Status = AssetReviewStatus.Approved;
        review.ReviewerId = reviewerId;
        review.Notes = notes.Trim();
        review.ReviewedAtUtc = DateTimeOffset.UtcNow;
        review.Asset.Status = AssetStatus.Published;
        review.Asset.CurrentVersionId = review.AssetVersionId;
        review.Asset.PublishedAtUtc ??= DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RejectAsync(string reviewId, string reviewerId, string notes, CancellationToken cancellationToken = default)
    {
        var review = await dbContext.AssetReviews
            .Include(x => x.Asset)
            .FirstOrDefaultAsync(x => x.ID == reviewId && x.Status == AssetReviewStatus.Pending, cancellationToken);
        if (review?.Asset is null)
        {
            return false;
        }

        review.Status = AssetReviewStatus.Rejected;
        review.ReviewerId = reviewerId;
        review.Notes = notes.Trim();
        review.ReviewedAtUtc = DateTimeOffset.UtcNow;
        review.Asset.Status = AssetStatus.Draft;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RevokeAsync(string reviewId, string reviewerId, string notes, CancellationToken cancellationToken = default)
    {
        var review = await dbContext.AssetReviews
            .Include(x => x.Asset)
            .FirstOrDefaultAsync(x => x.ID == reviewId && x.Status == AssetReviewStatus.Approved, cancellationToken);
        if (review?.Asset is null)
        {
            return false;
        }

        review.Status = AssetReviewStatus.Revoked;
        review.ReviewerId = reviewerId;
        review.Notes = notes.Trim();
        review.ReviewedAtUtc = DateTimeOffset.UtcNow;
        review.Asset.Status = AssetStatus.Offline;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static void RejectForRisk(
        Netor.Cortana.Platform.Entitys.Tables.Reviews.AssetReview review,
        string reviewerId,
        IReadOnlyList<string> issues)
    {
        review.Status = AssetReviewStatus.Rejected;
        review.ReviewerId = reviewerId;
        review.Notes = $"风控未通过：{string.Join("；", issues)}";
        review.ReviewedAtUtc = DateTimeOffset.UtcNow;
        if (review.Asset is not null)
        {
            review.Asset.Status = AssetStatus.Draft;
        }
    }

    private static string GetPackageIntegrityIssue(PackageIntegrityStatus status) => status switch
    {
        PackageIntegrityStatus.FileMissing => "资源包文件不存在或路径非法",
        PackageIntegrityStatus.HashMissing => "资源包缺少可信哈希",
        PackageIntegrityStatus.HashMismatch => "资源包哈希与记录不一致",
        PackageIntegrityStatus.ProviderMissing => "资源包存储提供方未注册",
        PackageIntegrityStatus.StorageUnavailable => "资源包存储服务不可用或权限异常",
        _ => "资源包完整性校验失败"
    };
}
