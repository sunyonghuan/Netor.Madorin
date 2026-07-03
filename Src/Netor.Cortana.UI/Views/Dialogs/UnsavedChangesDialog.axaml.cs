using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Netor.Cortana.UI.Views.Dialogs;

/// <summary>
/// 未保存内容确认对话框的用户选择。
/// </summary>
public enum UnsavedChoice
{
    /// <summary>
    /// 取消切换，留在当前模式。
    /// </summary>
    Cancel = 0,

    /// <summary>
    /// 切走 + 通过 ChatDraftService 暂存输入框 + 附件，回来时恢复。
    /// </summary>
    Save = 1,

    /// <summary>
    /// 切走 + 清空输入框 + 附件。
    /// </summary>
    Discard = 2,
}

/// <summary>
/// 未保存内容确认对话框（界面重设计 C2，决策 UI-7 D2 + DT-4）。
///
/// 用户在对话模式下输入了未发送的内容，主动切到工作流/群聊 tab 时触发。
/// 视觉规格详见 Docs/未来版本策划/界面重设计/03-交互细节.md §2。
///
/// 调用方式（典型）：
/// <code>
///   var choice = await UnsavedChangesDialog.ShowDialogAsync(
///       owner: this,
///       previewText: draftService.GetPreview(),
///       attachmentCount: draftService.AttachmentCount);
///   switch (choice)
///   {
///       case UnsavedChoice.Cancel: return;             // 留在当前
///       case UnsavedChoice.Save:                       // 切走，保留草稿
///           draftService.Save(InputBox.Text, _attachments);
///           break;
///       case UnsavedChoice.Discard:                    // 切走，丢弃草稿
///           InputBox.Text = string.Empty;
///           _attachments.Clear();
///           break;
///   }
///   // ...继续执行模式切换
/// </code>
/// </summary>
public partial class UnsavedChangesDialog : Window
{
    /// <summary>
    /// 用户最终选择（按下按钮后赋值，关闭对话框前由 ShowDialogAsync 读取）。
    /// </summary>
    private UnsavedChoice _result = UnsavedChoice.Cancel;

    /// <summary>
    /// 默认构造（XAML 加载需要无参构造）。
    /// </summary>
    public UnsavedChangesDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 静态便捷方法：弹出对话框并等待用户选择。
    /// </summary>
    /// <param name="owner">父窗口（必填，否则 ShowDialog 会抛 InvalidOperationException）。</param>
    /// <param name="previewText">输入框内容预览（典型：前 50 字截断）。</param>
    /// <param name="attachmentCount">附件数量。</param>
    /// <returns>用户选择的 <see cref="UnsavedChoice"/>。</returns>
    public static async Task<UnsavedChoice> ShowDialogAsync(
        Window owner,
        string previewText,
        int attachmentCount)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var dialog = new UnsavedChangesDialog();
        dialog.SetPreview(previewText, attachmentCount);

        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    /// <summary>
    /// 把预览内容 + 附件数量反映到 axaml 控件（XAML 中 x:Name="PreviewText" / "AttachmentCountText" / "PreviewBorder"）。
    /// 注意：这里访问的 PreviewText / PreviewBorder / AttachmentCountText 是 axaml 编译生成的字段，
    /// 不是类属性 — 因此本类不允许声明同名 public 属性（会与生成字段冲突）。
    /// </summary>
    private void SetPreview(string? previewText, int attachmentCount)
    {
        var preview = previewText ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(preview))
        {
            PreviewText.Text = preview;
            PreviewBorder.IsVisible = true;
        }

        var count = Math.Max(0, attachmentCount);
        if (count > 0)
        {
            AttachmentCountText.Text = $"{count} 个附件";
            AttachmentCountText.IsVisible = true;
        }
    }

    /// <summary>
    /// 取消按钮（也响应 ESC + Enter）：保持留在当前模式。
    /// </summary>
    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _result = UnsavedChoice.Cancel;
        Close();
    }

    /// <summary>
    /// 保留内容按钮：切走 + 调用方需通过 ChatDraftService.Save 暂存。
    /// </summary>
    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _result = UnsavedChoice.Save;
        Close();
    }

    /// <summary>
    /// 丢弃并切换按钮：切走 + 调用方需清空 InputBox + 附件。
    /// </summary>
    private void OnDiscardClick(object? sender, RoutedEventArgs e)
    {
        _result = UnsavedChoice.Discard;
        Close();
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
