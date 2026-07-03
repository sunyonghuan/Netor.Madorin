using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Accounts;
using Netor.Cortana.Platform.Entitys.Tables.Assets;

namespace Netor.Cortana.Platform.Entitys.Tables.Reviews;

/// <summary>
/// 资源审核记录。
/// </summary>
[Comment("资源审核记录")]
public sealed class AssetReview : Base
{
    [StringLength(32)]
    [Comment("资源ID")]
    [Display(Name = "资源ID")]
    public string AssetId { get; set; } = string.Empty;

    public Asset? Asset { get; set; }

    [StringLength(32)]
    [Comment("资源版本ID")]
    [Display(Name = "资源版本ID")]
    public string AssetVersionId { get; set; } = string.Empty;

    public AssetVersion? AssetVersion { get; set; }

    [StringLength(32)]
    [Comment("提交账号ID")]
    [Display(Name = "提交账号ID")]
    public string SubmitterAccountId { get; set; } = string.Empty;

    public Account? SubmitterAccount { get; set; }

    [Comment("审核状态")]
    [Display(Name = "审核状态")]
    public AssetReviewStatus Status { get; set; } = AssetReviewStatus.Pending;

    [StringLength(512)]
    [Comment("审核备注")]
    [Display(Name = "审核备注")]
    public string Notes { get; set; } = string.Empty;

    [StringLength(32)]
    [Comment("审核人ID")]
    [Display(Name = "审核人ID")]
    public string? ReviewerId { get; set; }

    [Comment("审核时间")]
    [Display(Name = "审核时间")]
    public DateTimeOffset? ReviewedAtUtc { get; set; }
}
