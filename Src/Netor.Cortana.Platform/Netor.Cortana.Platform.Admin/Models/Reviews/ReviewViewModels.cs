using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Services.Solutions;

namespace Netor.Cortana.Platform.Admin.Models.Reviews;

public sealed record ReviewListItem(
    string Id,
    string AssetId,
    string AssetName,
    string AssetSlug,
    AssetType AssetType,
    AssetStatus AssetStatus,
    string VersionName,
    string SubmitterName,
    AssetReviewStatus Status,
    string Notes,
    SolutionManifestSummary? SolutionSummary,
    IReadOnlyList<string> RiskIssues,
    string? ReviewerId,
    DateTimeOffset? ReviewedAtUtc,
    long TimeStamp);

public sealed record ReviewTableItem(
    string Id,
    string AssetId,
    string AssetName,
    string AssetSlug,
    AssetType AssetType,
    string AssetTypeName,
    AssetStatus AssetStatus,
    string AssetStatusName,
    string AssetStatusBadgeClass,
    string VersionName,
    string SubmitterName,
    AssetReviewStatus Status,
    string StatusName,
    string StatusBadgeClass,
    string Notes,
    SolutionManifestSummary? SolutionSummary,
    IReadOnlyList<string> RiskIssues,
    string ReviewerId,
    string ReviewedAtText,
    long TimeStamp);

public sealed class ReviewIndexViewModel
{
    public IReadOnlyList<ReviewListItem> Items { get; init; } = [];

    public string? Keyword { get; init; }

    public AssetReviewStatus? Status { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 10;

    public int TotalPages { get; init; }

    public int TotalCount { get; init; }

    public int PendingCount { get; init; }

    public int ApprovedCount { get; init; }

    public int RejectedCount { get; init; }

    public bool HasPreviousPage => Page > 1;

    public bool HasNextPage => Page < TotalPages;
}
