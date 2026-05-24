using System.ComponentModel;
using System.Runtime.CompilerServices;

using Netor.Cortana.Entitys.Services;
using Netor.Cortana.UI.Models;

namespace Netor.Cortana.UI.ViewModels;

/// <summary>
/// 主窗口 ViewModel（界面重设计 C2，决策 UI-3 B1 + UI-7 D2 + DT-3 B）。
///
/// 职责：
/// 1. 持有 <see cref="CurrentMode"/> 状态（Chat / Workflow / GroupChat），以 PropertyChanged 通知 axaml 绑定
/// 2. 启动时从 SystemSettings 恢复上次模式（决策 DT-3：记忆上次模式）
/// 3. 切换模式时主动持久化到 SystemSettings
/// 4. 提供 <see cref="ModeBoundLabel"/> 等派生属性，供顶栏 / 左侧 Tab2 等绑定
///
/// 设计权衡：
/// - 本期（C2）仅承载 CurrentMode + 持久化，不接管 InputBox 等内容控件的 binding
/// - InputBox / HistoryLabel 等 code-behind 操作保留不动（避免 C2 体量爆炸）
/// - C5 收尾时再考虑 Chat 全面 MVVM 化（但已识别为 v2 推后项）
///
/// 详见 Docs/未来版本策划/界面重设计/04-实施阶段.md §2.1 + 03-交互细节.md §1.1。
/// </summary>
public sealed class MainWindowVm : INotifyPropertyChanged
{
    /// <summary>
    /// 持久化 SystemSettings 的设置服务（C2 通过构造函数注入，由 DI 容器解析）。
    /// </summary>
    private readonly SystemSettingsService _settings;

    /// <summary>
    /// 当前工作模式（默认 Chat）。
    /// </summary>
    private WorkMode _currentMode = WorkMode.Chat;

    /// <summary>
    /// 构造 MainWindowVm。从 SystemSettings 恢复上次模式（决策 DT-3：记忆上次模式）。
    /// 异常 fallback 到 Chat 模式（保守策略，避免启动崩溃）。
    /// </summary>
    /// <param name="settings">SystemSettingsService 用于读写 <see cref="WorkModeExtensions.SettingsKey"/>。</param>
    public MainWindowVm(SystemSettingsService settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        try
        {
            var stored = _settings.GetValue(WorkModeExtensions.SettingsKey, "chat");
            _currentMode = WorkModeExtensions.FromPersistenceString(stored);
        }
        catch (Exception)
        {
            // 配置读取失败时静默 fallback 到 Chat（不阻塞 MainWindow 启动）
            _currentMode = WorkMode.Chat;
        }
    }

    /// <summary>
    /// 当前工作模式。Setter 会触发 PropertyChanged + 持久化。
    /// </summary>
    public WorkMode CurrentMode
    {
        get => _currentMode;
        set
        {
            if (_currentMode == value) return;
            _currentMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsChatMode));
            OnPropertyChanged(nameof(IsWorkflowMode));
            OnPropertyChanged(nameof(IsGroupChatMode));
            OnPropertyChanged(nameof(ModeBoundLabel));
            OnPropertyChanged(nameof(NewItemButtonText));
            OnPropertyChanged(nameof(SearchPlaceholder));
            OnPropertyChanged(nameof(RecentDropdownLabel));

            // 持久化到 SystemSettings（决策 DT-3）。
            // 持久化失败不影响 UI 切换，但记录到 Debug 输出。
            try
            {
                _settings.SetValue(WorkModeExtensions.SettingsKey, value.ToPersistenceString());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindowVm] 持久化 CurrentMode 失败：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 当前是否对话模式（用于 axaml IsVisible 绑定）。
    /// </summary>
    public bool IsChatMode => _currentMode == WorkMode.Chat;

    /// <summary>
    /// 当前是否工作流模式（用于 axaml IsVisible 绑定）。
    /// </summary>
    public bool IsWorkflowMode => _currentMode == WorkMode.Workflow;

    /// <summary>
    /// 当前是否群聊模式（用于 axaml IsVisible 绑定）。
    /// </summary>
    public bool IsGroupChatMode => _currentMode == WorkMode.GroupChat;

    /// <summary>
    /// 左侧 Tab2 文案（详见 01-布局规格.md §3.4 联动文案表）。
    /// </summary>
    public string ModeBoundLabel => _currentMode switch
    {
        WorkMode.Chat => "对话记录",
        WorkMode.Workflow => "工作记录",
        WorkMode.GroupChat => "会议记录",
        _ => "列表",
    };

    /// <summary>
    /// 左侧顶部 + 新建按钮文案（详见 01-布局规格.md §3.2）。
    /// </summary>
    public string NewItemButtonText => _currentMode switch
    {
        WorkMode.Chat => "+ 新建会话",
        WorkMode.Workflow => "+ 新建工作流任务",
        WorkMode.GroupChat => "+ 新建群聊",
        _ => "+ 新建",
    };

    /// <summary>
    /// 左侧搜索框 PlaceholderText（详见 01-布局规格.md §3.2）。
    /// </summary>
    public string SearchPlaceholder => _currentMode switch
    {
        WorkMode.Chat => "搜索会话标题...",
        WorkMode.Workflow => "搜索任务标题...",
        WorkMode.GroupChat => "搜索群聊标题...",
        _ => "搜索...",
    };

    /// <summary>
    /// 顶栏「最近 ▼」按钮文案（详见 01-布局规格.md §2.3）。
    /// </summary>
    public string RecentDropdownLabel => _currentMode switch
    {
        WorkMode.Chat => "最近会话 ▼",
        WorkMode.Workflow => "最近任务 ▼",
        WorkMode.GroupChat => "最近群聊 ▼",
        _ => "最近 ▼",
    };

    // ──── INotifyPropertyChanged ────

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
