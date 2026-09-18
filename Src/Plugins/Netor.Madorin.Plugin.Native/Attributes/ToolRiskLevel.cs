namespace Netor.Madorin.Plugin.Native;

/// <summary>
/// 工具风险级别。写在 <see cref="PluginAttribute"/> 上作为插件默认值，个别工具可通过 <see cref="ToolAttribute"/> 覆盖。
/// </summary>
public enum ToolRiskLevel
{
    /// <summary>低风险（只读操作）。适用于读取文件、查询数据、列表操作。</summary>
    Low,

    /// <summary>敏感读取。适用于读取密码、密钥、私有数据。</summary>
    SensitiveRead,

    /// <summary>写操作（可逆）。适用于创建/修改文件、写入数据库。</summary>
    Write,

    /// <summary>破坏性操作（不可逆）。适用于删除文件、清空数据、格式化。</summary>
    Destructive,

    /// <summary>进程操作。适用于启动/停止进程、执行命令。</summary>
    Process,

    /// <summary>PowerShell 脚本执行。适用于执行 PowerShell 脚本。</summary>
    PowerShell,

    /// <summary>网络操作。适用于 HTTP 请求、网络连接。</summary>
    Network
}
