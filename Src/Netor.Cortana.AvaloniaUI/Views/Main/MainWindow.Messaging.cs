using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

using Netor.Cortana.AvaloniaUI.Controls;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Extensions;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AvaloniaUI.Views;

/// <summary>
/// MainWindow — 消息发送 / 取消 / 渲染 / 滚动。
/// </summary>
public partial class MainWindow
{
    private static readonly Bitmap AiAvatarBitmap = LoadAiAvatarBitmap();
    private static readonly Bitmap UserAvatarBitmap = LoadUserAvatarBitmap();

    // AI 对话进行中标志 & 取消令牌
    private bool _isSending;

    private CancellationTokenSource? _sendCts;
    private Animation? _spinnerAnimation;

    // 用户是否手动向上滚动（此时不自动跟随）
    private bool _userScrolledUp;

    // ──────── 滚动控制 ────────

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var sv = MessageScroller;
        // 距底部不超过 30px 时视为"在底部"
        _userScrolledUp = sv.Offset.Y + sv.Viewport.Height < sv.Extent.Height - 30;
        ScrollToBottomBtn.IsVisible = _userScrolledUp;
    }

    private void OnScrollToBottomClick(object? sender, RoutedEventArgs e)
    {
        ForceScrollToBottom();
    }

    /// <summary>
    /// 滚动到消息列表底部（尊重用户手动上滚状态）。
    /// </summary>
    private void ScrollToBottom()
    {
        if (_userScrolledUp) return;
        Dispatcher.UIThread.Post(() => MessageScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// <summary>
    /// 自动滚动到底部（流式 token 时调用，尊重用户上滚）。
    /// </summary>
    internal void AutoScrollToBottom()
    {
        if (_userScrolledUp) return;
        Dispatcher.UIThread.Post(() => MessageScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// <summary>
    /// 强制滚动到底部（AI 回复结束时调用，重置上滚状态）。
    /// </summary>
    internal void ForceScrollToBottom()
    {
        _userScrolledUp = false;
        Dispatcher.UIThread.Post(() => MessageScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    // ──────── 发送 / 取消 ────────

    private void OnSendClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SendMessage();
    }

    private void OnStopClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CancelSending();
    }

    /// <summary>
    /// 取消正在进行的 AI 对话。
    /// </summary>
    private void CancelSending()
    {
        // 用户点 Stop：同步取消所有正在进行的语音/AI/TTS 任务，避免残留。
        try { App.Services.GetRequiredService<IVoiceCoordinator>().CancelEverything(); }
        catch { /* 启动极早期可能未注册，忽略 */ }

        _sendCts?.Cancel();
        var chatService = App.Services.GetRequiredService<AiChatHostedService>();
        chatService.CancelCurrentTask();
    }

    /// <summary>
    /// 切换发送图标 ↔ 转圈+停止 状态。
    /// </summary>
    private void SetSendingState(bool sending)
    {
        _isSending = sending;
        SendButton.IsVisible = !sending;
        StopButton.IsVisible = sending;

        if (sending)
        {
            _spinnerAnimation ??= new Animation
            {
                Duration = TimeSpan.FromSeconds(1),
                IterationCount = IterationCount.Infinite,
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(RotateTransform.AngleProperty, 0.0) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(RotateTransform.AngleProperty, 360.0) } },
                }
            };
            _spinnerAnimation.RunAsync(SpinnerIcon);
            return;
        }
        RefreshTokenProgress();
    }

    /// <summary>
    /// 刷新顶部 token 使用量进度条。取值来自 <see cref="AIAgentFactory"/>（跨 ChatClient 重建持久化）。
    /// 当没有任何真实用量上报时（LastInputTokens == 0）显示 0%，而不是伪造的最小值。
    /// </summary>
    private void RefreshTokenProgress()
    {
        var factory = App.Services.GetRequiredService<AIAgentFactory>();
        var max = factory.MaxContextTokens;
        if (max <= 0) return;

        Dispatcher.UIThread.Post(() =>
        {
            var total = max / 1000d;
            var used = factory.LastInputTokens / 1000d;
            var percent = total > 0 ? used / total * 100 : 0;
            AiProgressBar.Value = percent;
            AiProgressBar.Tag = factory.LastInputTokens > 0
                ? $"{percent:f1}%  {used:f1}k / {total:f0}k"
                : $"0%  0k / {total:f0}k";
        });
    }

    /// <summary>
    /// 发送用户消息并触发 AI 对话。
    /// </summary>
    private void SendMessage()
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text) && _attachments.Count == 0) return;

        // 用户做出"发送"决策：收尾所有进行中的语音/AI/TTS 任务。
        // 这样避免：主窗体下同时存在一条未完成的 STT 轮次 → 在发送后突然 publish OnSttFinal
        // 又打出一条用户气泡、并触发多路 AI 调用。
        try { App.Services.GetRequiredService<IVoiceCoordinator>().CancelEverything(); }
        catch { /* 启动极早期可能未注册，忽略 */ }

        // 隐藏欢迎面板
        HideWelcome();

        // 显示用户消息气泡（保留 #文件名 的显示形式）
        var displayText = text ?? string.Empty;
        if (_attachments.Count > 0)
        {
            var attachNames = string.Join(", ", _attachments.Select(a => $"📎 {a.Name}"));
            displayText = string.IsNullOrEmpty(displayText)
                ? attachNames
                : $"{displayText}\n{attachNames}";
        }
        AddMessageBubble(displayText, isUser: true);
        InputBox.Text = string.Empty;

        // 将 #文件名 替换为完整路径，传给 AI
        var resolvedText = ResolveFileReferences(text ?? string.Empty);
        _fileReferences.Clear();

        // 收集附件并清空 UI
        List<AttachmentInfo>? attachments = _attachments.Count > 0
            ? [.. _attachments]
            : null;
        ClearAttachments();

        // 收集 @智能体 提及并解析位置
        List<AgentMention>? mentions = null;
        if (_agentMentions.Count > 0)
        {
            mentions = [];
            foreach (var (name, agent) in _agentMentions)
            {
                var marker = $"@{name}";
                var idx = resolvedText.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    mentions.Add(new AgentMention(agent, idx, idx + marker.Length));
                }
            }
            _agentMentions.Clear();
            if (mentions.Count == 0) mentions = null;
        }

        // 通过 AiChatService 发送，AI 回复将通过 UiChatOutputChannel 渲染到界面
        var chatService = App.Services.GetRequiredService<AiChatHostedService>();
        var logger = App.Services.GetRequiredService<ILogger<MainWindow>>();

        _sendCts?.Dispose();
        _sendCts = new CancellationTokenSource();
        var token = _sendCts.Token;
        HistoryLabel.Text = resolvedText.Truncate(32);
        _ = Task.Run(async () =>
        {
            try
            {
                await chatService.SendMessageAsync(resolvedText, token, attachments, mentions);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("用户取消了对话");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "发送消息时出错");
                Dispatcher.UIThread.Post(() =>
                    AddMessageBubble($"⚠ 发送失败：{ex.Message}", isUser: false));
            }
            finally
            {
            }
        });
    }

    /// <summary>
    /// 将文本中的 #文件名 替换为完整路径。
    /// </summary>
    private string ResolveFileReferences(string text)
    {
        if (_fileReferences.Count == 0) return text;

        foreach (var (fileName, fullPath) in _fileReferences)
        {
            text = text.Replace($"#{fileName}", fullPath, StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    // ──────── 气泡渲染 ────────

    private static Bitmap LoadAiAvatarBitmap()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Cortana/Assets/logo.200.png"));
        return new Bitmap(stream);
    }

    private static Bitmap LoadUserAvatarBitmap()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Cortana/Assets/mk.png"));
        return new Bitmap(stream);
    }

    private static Control BuildUserAvatarGlyph(IBrush glyphBrush)
    {
        return new Image
        {
            Source = UserAvatarBitmap,
            Width = 32,
            Height = 32,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
    }

    /// <summary>
    /// 添加消息气泡到消息列表。
    /// </summary>
    /// <param name="authorName">作者显示名（null 时用户消息显示"我"，AI 消息显示当前智能体名）。</param>
    /// <param name="timestamp">消息时间（null 时使用当前时间）。</param>
    internal void AddMessageBubble(string content, bool isUser,
        IReadOnlyList<ChatMessageAssetEntity>? assets = null,
        string? authorName = null, DateTimeOffset? timestamp = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var userAvatarBrush = (IBrush)this.FindResource("UserAvatarBrush")!;
            var userAvatarBorderBrush = (IBrush)this.FindResource("UserAvatarBorderBrush")!;
            var userAvatarGlyphBrush = (IBrush)this.FindResource("UserAvatarGlyphBrush")!;
            var userBubbleBrush = (IBrush)this.FindResource("UserBubbleBrush")!;
            var userBubbleBorderBrush = (IBrush)this.FindResource("UserBubbleBorderBrush")!;
            var aiBubbleBrush = (IBrush)this.FindResource("AiBubbleBrush")!;
            var aiBubbleBorderBrush = (IBrush)this.FindResource("AiBubbleBorderBrush")!;
            var aiAvatarBorderBrush = (IBrush)this.FindResource("AiAvatarBorderBrush")!;
            var avatar = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(20),
                ClipToBounds = true,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Margin = isUser ? new Thickness(10, 0, 0, 0) : new Thickness(0, 0, 10, 0),
                Padding = isUser ? new Thickness(0) : new Thickness(1),
                Background = isUser ? userAvatarBrush : Brushes.Transparent,
                BorderBrush = isUser ? userAvatarBorderBrush : aiAvatarBorderBrush,
                BorderThickness = new Thickness(1),
                Child = isUser
                    ? BuildUserAvatarGlyph(userAvatarGlyphBrush)
                    : new Image
                    {
                        Source = AiAvatarBitmap,
                        Width = 40,
                        Height = 40,
                        Stretch = Stretch.UniformToFill,
                    }
            };

            // ── 气泡头部：作者名 + 时间 ──
            var displayName = authorName
                ?? (isUser ? "用户" : (ToolbarAgentLabel.Text ?? "助手"));
            var displayTime = (timestamp ?? DateTimeOffset.Now).ToLocalTime().ToString("HH:mm");

            var headerPanel = new DockPanel
            {
                Margin = isUser ? new Thickness(50, 0, 0, 2) : new Thickness(0, 0, 50, 2),
            };
            var nameBlock = new TextBlock
            {
                Text = displayName,
                FontSize = 11,
                FontWeight = FontWeight.Medium,
                Foreground = (IBrush)this.FindResource("SubtextBrush")!,
            };
            var timeBlock = new TextBlock
            {
                Text = displayTime,
                FontSize = 10,
                Foreground = (IBrush)this.FindResource("SubtextBrush")!,
                Opacity = 0.7,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };

            if (isUser)
            {
                nameBlock.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
                timeBlock.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
                timeBlock.SetValue(DockPanel.DockProperty, Avalonia.Controls.Dock.Right);
                nameBlock.SetValue(DockPanel.DockProperty, Avalonia.Controls.Dock.Right);
                headerPanel.Children.Add(timeBlock);
                headerPanel.Children.Add(nameBlock);
            }
            else
            {
                nameBlock.SetValue(DockPanel.DockProperty, Avalonia.Controls.Dock.Left);
                timeBlock.SetValue(DockPanel.DockProperty, Avalonia.Controls.Dock.Left);
                headerPanel.Children.Add(nameBlock);
                headerPanel.Children.Add(timeBlock);
            }

            // ── 气泡内容：Markdown + 可选资源卡片 ──
            Control bubbleContent;
            var markdown = new MarkdownRenderer { Markdown = content };

            var hasNonImageAssets = assets is { Count: > 0 } && assets.Any(a => a.AssetGroup != "images");
            if (hasNonImageAssets)
            {
                var appPaths = App.Services.GetRequiredService<IAppPaths>();
                var cardPanel = new ResourceCardPanel(assets!, appPaths.WorkspaceResourcesDirectory);
                bubbleContent = new StackPanel
                {
                    Spacing = 0,
                    Children = { markdown, cardPanel },
                };
            }
            else
            {
                bubbleContent = markdown;
            }

            // ── 消息气泡 ──
            var bubble = new Border
            {
                Background = isUser ? userBubbleBrush : aiBubbleBrush,
                BorderBrush = isUser ? userBubbleBorderBrush : aiBubbleBorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = isUser
                    ? new CornerRadius(4, 5, 4, 4)
                    : new CornerRadius(5, 4, 4, 4),
                Padding = new Thickness(14, 10),
                MinWidth = 120,
                // 对面留出头像等宽空白(40+10=50)，确保气泡不会顶到边缘
                Margin = isUser ? new Thickness(50, 0, 0, 0) : new Thickness(0, 0, 50, 0),
                Child = bubbleContent
            };

            // ── 消息行：头像 + (头部信息 + 气泡)（气泡占满剩余宽度） ──
            var bubbleColumn = new StackPanel { Spacing = 0 };
            bubbleColumn.Children.Add(headerPanel);
            bubbleColumn.Children.Add(bubble);

            var row = new DockPanel
            {
                LastChildFill = true,
            };

            avatar.SetValue(DockPanel.DockProperty, isUser ? Avalonia.Controls.Dock.Right : Avalonia.Controls.Dock.Left);

            if (isUser)
            {
                row.Children.Add(avatar);
                row.Children.Add(bubbleColumn);
            }
            else
            {
                row.Children.Add(avatar);
                row.Children.Add(bubbleColumn);
            }

            MessageList.Items.Add(row);
            ScrollToBottom();
        });
    }
}