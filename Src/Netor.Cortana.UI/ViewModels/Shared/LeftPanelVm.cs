using System.ComponentModel;
using System.Runtime.CompilerServices;

using Netor.Cortana.UI.Models.WorkMode;

namespace Netor.Cortana.UI.ViewModels.Shared;

/// <summary>
/// 左侧面板 ViewModel（界面重设计 C3，决策 UI-1 L2 + UI-9 底部 tab）。
///
/// 职责：
/// 1. 持有 <see cref="ActiveTabIndex"/> 状态（0 = 文件目录 / 1 = 动态 tab，随顶部模式变内容）
/// 2. 订阅 <see cref="MainWindowVm.PropertyChanged"/> 监听 CurrentMode 变化
/// 3. 模式切换时按 03-交互细节.md §4.2 规则联动 Tab：
///    - 用户当前在 Tab1（文件目录）：保持 Tab1（不打断）
///    - 用户当前在 Tab2：跟着模式切到新内容（保持 Tab2 激活）
/// 4. 暴露 <see cref="MainVm"/>，方便 axaml 直接绑定 ModeBoundLabel / NewItemButtonText / SearchPlaceholder
///
/// C3 阶段说明：
/// - Tab2 内容是占位（"待 C4 实现：工作流/群聊任务列表" 提示）
/// - C4 会把 TaskListPanel + ChatHistoryPanel 接入 Tab2
/// - 联动逻辑（CurrentMode → Tab2 文案/内容）现在就建立好，C4 时只需挂内容
///
/// 详见 Docs/未来版本策划/界面重设计/01-布局规格.md §3 + 03-交互细节.md §4。
/// </summary>
public sealed class LeftPanelVm : INotifyPropertyChanged
{
    /// <summary>
    /// 主窗口 VM（C3 通过构造函数注入，由 DI 容器解析）。
    /// 公开此引用是为了 axaml 能直接绑定到 <see cref="MainWindowVm.ModeBoundLabel"/> 等派生属性。
    /// </summary>
    public MainWindowVm MainVm { get; }

    /// <summary>
    /// 当前激活 tab 索引：0 = 文件目录 / 1 = 动态 tab。
    /// </summary>
    private int _activeTabIndex;

    /// <summary>
    /// 构造 LeftPanelVm。订阅 MainVm.PropertyChanged 实现 Tab 联动。
    /// </summary>
    /// <param name="mainVm">主窗口 VM（DI 注入，单例）。</param>
    public LeftPanelVm(MainWindowVm mainVm)
    {
        MainVm = mainVm ?? throw new ArgumentNullException(nameof(mainVm));
        MainVm.PropertyChanged += OnMainVmPropertyChanged;
    }

    /// <summary>
    /// 当前激活 tab 索引：0 = 文件目录 / 1 = 动态 tab。Setter 触发 PropertyChanged。
    /// </summary>
    public int ActiveTabIndex
    {
        get => _activeTabIndex;
        set
        {
            if (_activeTabIndex == value) return;
            _activeTabIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTab1Active));
            OnPropertyChanged(nameof(IsTab2Active));
        }
    }

    /// <summary>
    /// 当前是否激活 Tab1（文件目录）。
    /// </summary>
    public bool IsTab1Active => _activeTabIndex == 0;

    /// <summary>
    /// 当前是否激活 Tab2（动态 tab）。
    /// </summary>
    public bool IsTab2Active => _activeTabIndex == 1;

    /// <summary>
    /// 监听主 VM 模式变化 → 按 03-交互细节.md §4.2 规则联动 Tab。
    /// 用户当前在 Tab2 时跟着切（继续看列表）；在 Tab1 时不打断（继续看文件目录）。
    /// </summary>
    private void OnMainVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 仅 CurrentMode 变化时触发联动
        if (e.PropertyName != nameof(MainWindowVm.CurrentMode)) return;

        // 用户在 Tab1 时不打断 — 本 VM 不主动切；
        // 用户在 Tab2 时保持 Tab2 激活（虽然这一步 ActiveTabIndex 不变，触发文案 / 内容 binding 更新是 axaml 通过 MainVm.PropertyChanged 自然完成的）
        // 总之 C3 阶段联动只表现为：Tab2 头部文案 + Tab2 内容容器跟随 MainVm.ModeBoundLabel / IsXxxMode 变化。
        // 此 hook 留作 C4+ 扩展点。
    }

    // ──── INotifyPropertyChanged ────

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
