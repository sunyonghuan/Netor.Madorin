using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Netor.Cortana.Platform.Entitys.Data;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Assets;
using Netor.Cortana.Platform.Entitys.Tables.Creators;
using Netor.Cortana.Platform.Entitys.Tables.Reviews;
using Netor.Cortana.Platform.Services.Packages;

namespace Netor.Cortana.Platform.Services.Creators;

/// <summary>
/// 创作者资料与资源提交服务。
/// </summary>
public sealed class CreatorService(PlatformDbContext dbContext, PackageStorageService packageStorageService)
{
    public async Task<CreatorProfile> ApplyAsync(string accountId, string displayName, string bio, CancellationToken cancellationToken = default)
    {
        var profile = await dbContext.CreatorProfiles.FirstOrDefaultAsync(x => x.AccountId == accountId, cancellationToken);
        if (profile is null)
        {
            profile = dbContext.CreatorProfiles.Add(new CreatorProfile
            {
                AccountId = accountId,
                DisplayName = displayName.Trim(),
                Bio = bio.Trim(),
                Status = CreatorStatus.Pending
            }).Entity;
        }
        else
        {
            profile.DisplayName = displayName.Trim();
            profile.Bio = bio.Trim();
            if (profile.Status == CreatorStatus.Suspended)
            {
                profile.Status = CreatorStatus.Pending;
                profile.ApprovedAtUtc = null;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return profile;
    }

    public async Task<bool> ApproveCreatorAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var profile = await dbContext.CreatorProfiles.FirstOrDefaultAsync(x => x.AccountId == accountId, cancellationToken);
        if (profile is null)
        {
            return false;
        }

        profile.Status = CreatorStatus.Approved;
        profile.ApprovedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<CreatorAssetSubmissionResult> SubmitAssetAsync(
        string accountId,
        CreatorAssetSubmission submission,
        CancellationToken cancellationToken = default)
    {
        var creator = await dbContext.CreatorProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.AccountId == accountId, cancellationToken);
        if (creator?.Status != CreatorStatus.Approved)
        {
            return CreatorAssetSubmissionResult.NotApproved();
        }

        var slugExists = await dbContext.Assets.AnyAsync(x => x.Slug == submission.Slug, cancellationToken);
        if (slugExists)
        {
            return CreatorAssetSubmissionResult.SlugExists();
        }

        if (!IsPackageReferenceMatched(submission))
        {
            return CreatorAssetSubmissionResult.InvalidPackageReference();
        }

        var storageProvider = await packageStorageService.GetAvailableProviderNameOrDefaultAsync(submission.StorageProvider, cancellationToken);
        if (storageProvider is null)
        {
            return CreatorAssetSubmissionResult.InvalidStorageProvider();
        }

        var asset = dbContext.Assets.Add(new Asset
        {
            OwnerAccountId = accountId,
            Type = submission.Type,
            CategoryId = submission.CategoryId,
            Name = submission.Name,
            Slug = submission.Slug,
            DeveloperName = creator.DisplayName,
            ShortDescription = submission.ShortDescription,
            Description = submission.Description,
            Tags = submission.Tags,
            Status = AssetStatus.Draft,
            Versions =
            [
                new AssetVersion
                {
                    VersionName = submission.VersionName,
                    ReleaseNotes = submission.ReleaseNotes,
                    ManifestJson = submission.ManifestJson,
                    PackageHash = submission.PackageHash,
                    StorageProvider = storageProvider,
                    PackageSize = submission.PackageSize,
                    FilePath = submission.FilePath
                }
            ],
            PricingPlans =
            [
                new PricingPlan
                {
                    Name = submission.Price <= 0 ? "免费版" : "创作者订阅",
                    PlanType = submission.Price <= 0 ? PricingPlanType.Free : PricingPlanType.Monthly,
                    Price = submission.Price,
                    Currency = submission.Currency,
                    DurationDays = submission.Price <= 0 ? 0 : Math.Max(1, submission.DurationDays),
                    IsActive = true
                }
            ]
        }).Entity;

        var version = asset.Versions.Single();
        var review = dbContext.AssetReviews.Add(new AssetReview
        {
            Asset = asset,
            AssetVersion = version,
            SubmitterAccountId = accountId,
            Status = AssetReviewStatus.Pending,
            Notes = "创作者提交，等待平台审核。"
        }).Entity;

        await dbContext.SaveChangesAsync(cancellationToken);
        return CreatorAssetSubmissionResult.Created(asset.ID, version.ID, review.ID);
    }

    private static bool IsPackageReferenceMatched(CreatorAssetSubmission submission)
    {
        var normalizedPath = submission.FilePath.Replace('\\', '/');
        var packageSegmentIndex = normalizedPath.IndexOf("packages/", StringComparison.OrdinalIgnoreCase);
        if (packageSegmentIndex >= 0)
        {
            normalizedPath = normalizedPath[packageSegmentIndex..];
        }

        var expectedPrefix = $"packages/{GetAssetTypeFolder(submission.Type)}/{GetSafePathSegment(submission.Slug, "asset")}/{GetSafePathSegment(submission.VersionName, "version")}/";
        return normalizedPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetSafePathSegment(string value, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var chars = normalized
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '-')
            .ToArray();
        var segment = new string(chars).Trim('.', '-');
        return string.IsNullOrWhiteSpace(segment) ? fallback : segment;
    }

    private static string GetAssetTypeFolder(AssetType type) => type switch
    {
        AssetType.Plugin => "plugins",
        AssetType.Skill => "skills",
        AssetType.Agent => "agents",
        AssetType.Solution => "solutions",
        _ => "assets"
    };
}

public sealed record CreatorAssetSubmission(
    AssetType Type,
    string? CategoryId,
    string Name,
    string Slug,
    string ShortDescription,
    string Description,
    string Tags,
    string VersionName,
    string ReleaseNotes,
    string ManifestJson,
    string PackageHash,
    string StorageProvider,
    long PackageSize,
    string FilePath,
    decimal Price,
    string Currency,
    int DurationDays);

public sealed record CreatorAssetSubmissionResult(
    CreatorAssetSubmissionStatus Status,
    string? AssetId,
    string? AssetVersionId,
    string? ReviewId)
{
    public static CreatorAssetSubmissionResult Created(string assetId, string assetVersionId, string reviewId)
        => new(CreatorAssetSubmissionStatus.Created, assetId, assetVersionId, reviewId);

    public static CreatorAssetSubmissionResult NotApproved()
        => new(CreatorAssetSubmissionStatus.CreatorNotApproved, null, null, null);

    public static CreatorAssetSubmissionResult SlugExists()
        => new(CreatorAssetSubmissionStatus.SlugExists, null, null, null);

    public static CreatorAssetSubmissionResult InvalidPackageReference()
        => new(CreatorAssetSubmissionStatus.InvalidPackageReference, null, null, null);

    public static CreatorAssetSubmissionResult InvalidStorageProvider()
        => new(CreatorAssetSubmissionStatus.InvalidStorageProvider, null, null, null);
}

public enum CreatorAssetSubmissionStatus
{
    [Display(Name = "已创建")]
    Created,

    [Display(Name = "创作者未通过")]
    CreatorNotApproved,

    [Display(Name = "标识已存在")]
    SlugExists,

    [Display(Name = "包引用不匹配")]
    InvalidPackageReference,

    [Display(Name = "存储提供方无效")]
    InvalidStorageProvider
}
