using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Netor.Cortana.UI.Controls.Common;

/// <summary>
/// 聊天消息气泡控件，区分用户消息和 AI 消息的显示样式。
/// </summary>
public sealed class ChatBubble : ContentControl
{
    /// <summary>
    /// 是否为用户发送的消息。
    /// </summary>
    public static readonly StyledProperty<bool> IsUserMessageProperty =
        AvaloniaProperty.Register<ChatBubble, bool>(nameof(IsUserMessage));

    public bool IsUserMessage
    {
        get => GetValue(IsUserMessageProperty);
        set => SetValue(IsUserMessageProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        UpdateVisualState();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsUserMessageProperty)
        {
            UpdateVisualState();
        }
    }

    private void UpdateVisualState()
    {
        if (IsUserMessage)
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            Background = new SolidColorBrush(Color.FromArgb(40, 86, 156, 214));
            CornerRadius = new CornerRadius(12, 12, 2, 12);
        }
        else
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255));
            CornerRadius = new CornerRadius(12, 12, 12, 2);
        }
    }
}
