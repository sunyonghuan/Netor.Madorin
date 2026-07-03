using Netor.Cortana.Entitys;

namespace Netor.Cortana.UI.Services;

/// <summary>
/// 对话草稿暂存服务（界面重设计 C2，决策 UI-7 D2）。
///
/// 用途：用户在对话模式下输入未发送的文本 + 附件，主动切到工作流 / 群聊模式时，
/// 弹出 "未保存内容" 确认对话框，用户选 "保留内容" → 调用 <see cref="Save"/>；
/// 切回对话模式时，调用 <see cref="Restore"/> 恢复输入框。
///
/// 设计决策（详见 Docs/未来版本策划/界面重设计/03-交互细节.md §1.4）：
/// - <b>内存级单例</b>：进程退出即丢失，不持久化到 SQLite，避免敏感对话留盘
/// - <b>一次性恢复</b>：调 Restore 后内部状态自动清空，下次再调返回 null
/// - <b>per-process 单例</b>：阶段 7+ 引入多窗口时改为 per-window，本期不考虑
/// - <b>线程安全</b>：用 lock 保护内部状态，避免并发 Save/Restore 撕裂
///
/// 在 DI 中以 Singleton 形式注册（详见 App.axaml.cs ConfigureServices）。
/// </summary>
public sealed class ChatDraftService
{
    private readonly object _lock = new();
    private string? _text;
    private IReadOnlyList<AttachmentInfo> _attachments = Array.Empty<AttachmentInfo>();

    /// <summary>
    /// 当前是否有草稿（用于 UI 显示提示徽章 / 确认对话框预览）。
    /// </summary>
    public bool HasDraft
    {
        get
        {
            lock (_lock)
            {
                return !string.IsNullOrWhiteSpace(_text) || _attachments.Count > 0;
            }
        }
    }

    /// <summary>
    /// 当前草稿文本预览（取前 N 字，仅用于对话框预览，不修改内部状态）。
    /// </summary>
    /// <param name="maxLength">预览最大字符数，默认 50。</param>
    /// <returns>预览字符串；草稿不存在时返回空字符串。</returns>
    public string GetPreview(int maxLength = 50)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(_text)) return string.Empty;
            return _text.Length <= maxLength
                ? _text
                : _text.Substring(0, maxLength) + "…";
        }
    }

    /// <summary>
    /// 当前草稿附件数（仅用于对话框预览）。
    /// </summary>
    public int AttachmentCount
    {
        get
        {
            lock (_lock)
            {
                return _attachments.Count;
            }
        }
    }

    /// <summary>
    /// 暂存草稿（决策 UI-7 D2 "保留内容" 分支调用）。
    /// 后续调用会覆盖前次暂存。
    /// </summary>
    /// <param name="text">输入框文本（可为 null 或空，表示无文本）。</param>
    /// <param name="attachments">附件列表（可为 null 或空集合）。</param>
    public void Save(string? text, IEnumerable<AttachmentInfo>? attachments)
    {
        lock (_lock)
        {
            _text = text;
            _attachments = attachments is null
                ? Array.Empty<AttachmentInfo>()
                : new List<AttachmentInfo>(attachments).AsReadOnly();
        }
    }

    /// <summary>
    /// 恢复草稿（一次性，调用后内部状态自动清空）。
    /// </summary>
    /// <returns>
    /// (Text, Attachments) 元组：
    /// - 调用前无草稿 → (null, 空集合)；
    /// - 调用前有草稿 → 返回保存的内容，内部状态清空，下次再调返回 (null, 空集合)。
    /// </returns>
    public (string? Text, IReadOnlyList<AttachmentInfo> Attachments) Restore()
    {
        lock (_lock)
        {
            var text = _text;
            var attachments = _attachments;
            _text = null;
            _attachments = Array.Empty<AttachmentInfo>();
            return (text, attachments);
        }
    }

    /// <summary>
    /// 主动清空草稿（用于 "丢弃并切换" 分支或测试场景）。
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _text = null;
            _attachments = Array.Empty<AttachmentInfo>();
        }
    }
}
