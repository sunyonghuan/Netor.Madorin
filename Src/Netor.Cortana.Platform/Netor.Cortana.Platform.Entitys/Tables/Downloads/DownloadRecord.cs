using Netor.Cortana.Platform.Entitys.Tables.Accounts;
using Netor.Cortana.Platform.Entitys.Tables.Assets;
using Netor.Cortana.Platform.Entitys.Tables.Subscriptions;

namespace Netor.Cortana.Platform.Entitys.Tables.Downloads;

/// <summary>
/// 平台下载记录。
/// </summary>
[Comment("平台下载记录")]
public sealed class DownloadRecord : Base
{
    public string AccountId { get; set; } = string.Empty;

    public Account? Account { get; set; }
    public string AssetId { get; set; } = string.Empty;

    public Asset? Asset { get; set; }
    public string AssetVersionId { get; set; } = string.Empty;

    public AssetVersion? AssetVersion { get; set; }
    public string? SubscriptionId { get; set; }

    public Subscription? Subscription { get; set; }
    [StringLength(64)]
    public string? IpAddress { get; set; }

    [StringLength(512)]
    public string? UserAgent { get; set; }
}