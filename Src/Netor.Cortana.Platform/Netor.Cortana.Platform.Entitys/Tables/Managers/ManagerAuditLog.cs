namespace Netor.Cortana.Platform.Entitys.Tables.Managers;

/// <summary>
/// 平台管理员操作审计日志。
/// </summary>
[Comment("平台管理员操作审计日志")]
public sealed class ManagerAuditLog : Base
{
    [StringLength(32)]
    [Comment("管理员ID")]
    [Display(Name = "管理员ID")]
    public string ManagerId { get; set; } = string.Empty;

    public Manager? Manager { get; set; }

    [StringLength(32)]
    [Comment("MCP令牌ID")]
    [Display(Name = "MCP令牌ID")]
    public string? TokenId { get; set; }

    [StringLength(128)]
    [Comment("工具或动作名称")]
    [Display(Name = "工具或动作名称")]
    public string ToolName { get; set; } = string.Empty;

    [StringLength(16)]
    [Comment("来源")]
    [Display(Name = "来源")]
    public string SourceName { get; set; } = string.Empty;

    [StringLength(64)]
    [Comment("IP地址")]
    [Display(Name = "IP地址")]
    public string? Ip { get; set; }

    [StringLength(256)]
    [Comment("User-Agent")]
    [Display(Name = "User-Agent")]
    public string? Ua { get; set; }

    [Comment("是否成功")]
    [Display(Name = "是否成功")]
    public bool Success { get; set; }

    [StringLength(64)]
    [Comment("错误码")]
    [Display(Name = "错误码")]
    public string? ErrorCode { get; set; }

    [Comment("耗时毫秒")]
    [Display(Name = "耗时毫秒")]
    public int DurationMs { get; set; }

    [Comment("创建时间")]
    [Display(Name = "创建时间")]
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
