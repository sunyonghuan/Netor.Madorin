namespace Netor.Cortana.Platform.Web.Models.UserCenter;

public sealed class UserCenterIndexViewModel
{
    public string DisplayName { get; init; } = "演示用户";

    public IReadOnlyList<UserSubscriptionItem> ExpiringSubscriptions { get; init; } = [];

    public int SubscriptionCount { get; init; }

    public int DownloadCount { get; init; }

    public int OrderCount { get; init; }
}

public sealed class UserSubscriptionListViewModel
{
    public IReadOnlyList<UserSubscriptionItem> Items { get; init; } = [];
}

public sealed class UserDownloadListViewModel
{
    public IReadOnlyList<UserDownloadItem> Items { get; init; } = [];
}

public sealed class UserOrderListViewModel
{
    public IReadOnlyList<UserOrderItem> Items { get; init; } = [];
}

public sealed class UserProfileViewModel
{
    public string LoginUserName { get; init; } = string.Empty;

    public string? Email { get; init; }

    public string? Phone { get; init; }

    public string? NickName { get; init; }

    public string? RealName { get; init; }
}

public sealed record UserSubscriptionItem(
    string Id,
    string AssetId,
    string PricingPlanId,
    string AssetName,
    string AssetTypeName,
    string PricingPlanName,
    string PlanTypeName,
    string StatusName,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int RemainingDays,
    bool IsExpiringSoon,
    bool IsExpired);

public sealed record UserDownloadItem(string AssetName, string VersionName, string? IpAddress, long CreatedAt);

public sealed record UserOrderItem(string Id, string OrderNo, string Title, decimal Amount, string StatusName, long CreatedAt);