using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Netor.Cortana.UI.Controls.WorkMode;

/// <summary>
/// 工具/沙盒授权浮窗。
///
/// 视觉规范（09-界面交互设计.md §3.8）：
/// - 显示在输入框上方，从下往上滑入（Y: 30→0 + Opacity: 0→1）
/// - 全局唯一交互按钮区
/// - 4 按钮 + 提示用户走输入框的"其他方案"
/// </summary>
public enum ApprovalDecision
{
    ApproveOnce,
    ApproveSession,
    ApproveAlways,
    Reject,
    Alternative,
}

public partial class ApprovalFloatPopup : UserControl
{
    public event EventHandler<ApprovalDecision>? DecisionMade;
    public event EventHandler<string>? AlternativeSubmitted;

    public ApprovalFloatPopup()
    {
        InitializeComponent();
    }

    /// <summary>显示授权请求。</summary>
    public void Show(string toolName, string? paramText = null, string? risk = null)
    {
        ToolNameBlock.Text = toolName;
        ParamsBlock.Text = paramText ?? string.Empty;
        ParamsBlock.IsVisible = !string.IsNullOrWhiteSpace(paramText);
        RiskBlock.Text = risk ?? string.Empty;
        RiskBlock.IsVisible = !string.IsNullOrWhiteSpace(risk);
        AlternativeInput.Text = string.Empty;

        IsVisible = true;
        // 强制重置到藏起来的位置，再启动过渡（确保滑入动画可见）
        RootBorder.Opacity = 0;
        if (RootBorder.RenderTransform is TranslateTransform tt) tt.Y = 30;

        // 下一个 UI 帧再切到目标值，触发 Transitions
        Dispatcher.UIThread.Post(() =>
        {
            RootBorder.Opacity = 1;
            if (RootBorder.RenderTransform is TranslateTransform tt2) tt2.Y = 0;
            AlternativeInput.Focus();
        }, DispatcherPriority.Render);
    }

    /// <summary>隐藏（带退出动画）。</summary>
    public void Hide()
    {
        RootBorder.Opacity = 0;
        if (RootBorder.RenderTransform is TranslateTransform tt) tt.Y = 30;
        // 等过渡完成后真正隐藏
        DispatcherTimer.RunOnce(() => IsVisible = false, TimeSpan.FromMilliseconds(240));
    }

    private void OnApproveOnceClick(object? sender, RoutedEventArgs e)
        => Raise(ApprovalDecision.ApproveOnce);

    private void OnApproveSessionClick(object? sender, RoutedEventArgs e)
        => Raise(ApprovalDecision.ApproveSession);

    private void OnApproveAlwaysClick(object? sender, RoutedEventArgs e)
        => Raise(ApprovalDecision.ApproveAlways);

    private void OnRejectClick(object? sender, RoutedEventArgs e)
        => Raise(ApprovalDecision.Reject);

    private void OnAlternativeInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        var text = AlternativeInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        AlternativeSubmitted?.Invoke(this, text);
        DecisionMade?.Invoke(this, ApprovalDecision.Alternative);
        Hide();
    }

    private void Raise(ApprovalDecision d)
    {
        DecisionMade?.Invoke(this, d);
        Hide();
    }
}
