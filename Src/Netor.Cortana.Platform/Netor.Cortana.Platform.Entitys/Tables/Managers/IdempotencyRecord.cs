namespace Netor.Cortana.Platform.Entitys.Tables.Managers;

/// <summary>
/// MCP 写工具幂等记录。
/// </summary>
[Comment("MCP 写工具幂等记录")]
public sealed class IdempotencyRecord : Base
{
    [StringLength(64)]
    [Comment("幂等哈希")]
    [Display(Name = "幂等哈希")]
    public string Hash { get; set; } = string.Empty;

    [StringLength(128)]
    [Comment("工具名称")]
    [Display(Name = "工具名称")]
    public string ToolName { get; set; } = string.Empty;

    [StringLength(32)]
    [Comment("管理员ID")]
    [Display(Name = "管理员ID")]
    public string ManagerId { get; set; } = string.Empty;

    [StringLength(128)]
    [Comment("请求ID")]
    [Display(Name = "请求ID")]
    public string RequestId { get; set; } = string.Empty;

    [Comment("结果JSON")]
    [Display(Name = "结果JSON")]
    public string ResultJson { get; set; } = string.Empty;

    [Comment("创建时间")]
    [Display(Name = "创建时间")]
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}
