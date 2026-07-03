namespace Netor.Cortana.Entitys;

/// <summary>
/// 窗口控制契约，解耦业务层对 UI 窗口的直接依赖。
/// 由 UI 壳实现并注册到 DI，业务层通过此接口查询/操作窗口状态。
/// </summary>
public interface IWindowController
{
    /// <summary>显示主窗口。</summary>
    void ShowMainWindow();

    /// <summary>隐藏主窗口。</summary>
    void HideMainWindow();

    /// <summary>显示设置窗口。</summary>
    void ShowSettingsWindow();

    /// <summary>显示浮动窗口。</summary>
    void ShowFloatWindow();

    /// <summary>移动浮动窗口到指定位置。</summary>
    void MoveFloatWindow(int x, int y);

    /// <summary>主窗口是否可见。</summary>
    bool IsMainWindowVisible();
}
