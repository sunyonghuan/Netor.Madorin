namespace Netor.Cortana.Platform.Entitys.Tables.Managers;

/// <summary>
/// 平台管理员 MCP 访问令牌。
/// </summary>
[Comment("平台管理员 MCP 访问令牌")]
public sealed class ManagerMcpToken : Base
{
    [StringLength(32)]
    [Comment("管理员ID")]
    [Display(Name = "管理员ID")]
    public string ManagerId { get; set; } = string.Empty;

    public Manager? Manager { get; set; }

    [StringLength(64)]
    [Comment("令牌哈希")]
    [Display(Name = "令牌哈希")]
    public string TokenHash { get; set; } = string.Empty;

    [StringLength(12)]
    [Comment("令牌前缀")]
    [Display(Name = "令牌前缀")]
    public string TokenPrefix { get; set; } = string.Empty;

    [StringLength(64)]
    [Comment("备注")]
    [Display(Name = "备注")]
    public string Note { get; set; } = string.Empty;

    [Comment("是否启用")]
    [Display(Name = "是否启用")]
    public bool Enabled { get; set; } = true;

    [Comment("创建时间")]
    [Display(Name = "创建时间")]
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    [Comment("最近使用时间")]
    [Display(Name = "最近使用时间")]
    public DateTimeOffset? LastUsedUtc { get; set; }

    [StringLength(64)]
    [Comment("最近使用IP")]
    [Display(Name = "最近使用IP")]
    public string? LastUsedIp { get; set; }

    [StringLength(256)]
    [Comment("最近使用UA")]
    [Display(Name = "最近使用UA")]
    public string? LastUsedUa { get; set; }
}
