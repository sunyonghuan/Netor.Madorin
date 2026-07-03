namespace Netor.Cortana.UI.Models.WorkMode;

/// <summary>
/// 工作模式枚举（界面重设计 C2，决策 UI-3 B1）。
///
/// 主窗口顶栏 tab 对应三个工作模式：
/// - <see cref="Chat"/>     : 对话模式（默认，与现有 Chat Tab 一致）
/// - <see cref="Workflow"/> : 工作流模式（多 Agent 协作，SubMode in magentic/parallelanalysis）
/// - <see cref="GroupChat"/>: 群聊模式（多 Agent 轮流发言，SubMode = groupchat）
///
/// 详见 Docs/未来版本策划/界面重设计/01-布局规格.md §2.2 + 03-交互细节.md §1.1。
/// </summary>
public enum WorkMode
{
    /// <summary>
    /// 对话模式（默认）。
    /// </summary>
    Chat = 0,

    /// <summary>
    /// 工作流模式（Magentic / ParallelAnalysis 子模式）。
    /// </summary>
    Workflow = 1,

    /// <summary>
    /// 群聊模式（GroupChat 子模式）。
    /// </summary>
    GroupChat = 2,
}

/// <summary>
/// <see cref="WorkMode"/> 扩展方法：与字符串 / 持久化值之间的转换。
/// </summary>
public static class WorkModeExtensions
{
    /// <summary>
    /// SystemSettings 持久化 key（决策 DT-3：记忆上次模式）。
    /// </summary>
    public const string SettingsKey = "UI.MainWindow.CurrentMode";

    /// <summary>
    /// 转换为持久化字符串（小写，与 axaml Tag 值一致）。
    /// </summary>
    public static string ToPersistenceString(this WorkMode mode) => mode switch
    {
        WorkMode.Chat => "chat",
        WorkMode.Workflow => "workflow",
        WorkMode.GroupChat => "groupchat",
        _ => "chat",
    };

    /// <summary>
    /// 从持久化字符串解析（容错：未知值 fallback 到 Chat）。
    /// 支持别名：workspace（兼容 5B-Phase3 时期的 Tag 值）。
    /// </summary>
    public static WorkMode FromPersistenceString(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "chat" => WorkMode.Chat,
            "workflow" => WorkMode.Workflow,
            "workspace" => WorkMode.Workflow,    // 兼容旧值（C2 之前用 workspace 作 tag）
            "groupchat" => WorkMode.GroupChat,
            _ => WorkMode.Chat,
        };
    }

    /// <summary>
    /// 转换为用户可见的中文文案（顶栏 tab 显示用）。
    /// </summary>
    public static string ToDisplayText(this WorkMode mode) => mode switch
    {
        WorkMode.Chat => "对话",
        WorkMode.Workflow => "工作流",
        WorkMode.GroupChat => "群聊",
        _ => "对话",
    };
}
