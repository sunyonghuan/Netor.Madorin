using System.Diagnostics;
using System.Text;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.UI.Controls.Common;

public partial class RealtimeProcessCard : UserControl, IDisposable
{
    private readonly StringBuilder _contentBuffer = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Image? _arrowImage;

    /// <summary>
    /// 单卡输出字符数上限（约 256 KB，UTF-16 每字符 2 字节）。
    /// 超限后停止追加，FlushContent 追加截断提示，避免 TextBlock 全量重排成本无限增长。
    /// </summary>
    private const int MaxContentChars = 128 * 1024; // 128 K chars = 256 KB

    private bool _isDirty;
    private bool _isTruncated;
    private bool _isExpanded = true;
    private bool _manualToggled;
    private bool _disposed;
    private string _status = "running";
    private int? _exitCode;
    private long _durationMs;
    private bool IsThinking => string.Equals(Kind, "thinking", StringComparison.OrdinalIgnoreCase);

    public RealtimeProcessCard()
        : this(new RealtimeProcessEvent
        {
            ProcessId = Guid.NewGuid().ToString("N"),
            Kind = "tool",
            Title = "过程",
            Status = "running",
            Timestamp = DateTimeOffset.UtcNow,
        })
    {
    }

    public RealtimeProcessCard(RealtimeProcessEvent initial)
    {
        InitializeComponent();
        _arrowImage = this.FindControl<Image>("ArrowImage");

        ProcessId = initial.ProcessId;
        Title = string.IsNullOrWhiteSpace(initial.Title) ? "过程" : initial.Title.Trim();
        Kind = string.IsNullOrWhiteSpace(initial.Kind) ? "tool" : initial.Kind.Trim();

        HeaderButton.Cursor = new Cursor(StandardCursorType.Hand);
        HeaderButton.Click += OnHeaderClick;

        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => FlushContent());
        _flushTimer.Start();

        ApplyTextBrushes();
        UpdateHeader(initial.Status, initial.ExitCode, initial.DurationMs);
        AppendContent(initial.Content);
        SetExpanded(true);
    }

    public string ProcessId { get; }

    public string Title { get; }

    public string Kind { get; }

    public string Status => _status;

    public int? ExitCode => _exitCode;

    public long DurationMs => _durationMs;

    public void UpdateStatus(string status, int? exitCode, long durationMs)
    {
        Dispatcher.UIThread.Post(() => UpdateHeader(status, exitCode, durationMs));
    }

    public void AppendContent(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        void Append()
        {
            // 已截断：不再追加任何内容
            if (_isTruncated)
                return;

            // 超限检测：追加后是否会超过 256 KB 上限
            if (_contentBuffer.Length + content.Length > MaxContentChars)
            {
                // 尽量追加剩余空间内能容纳的部分
                var remaining = MaxContentChars - _contentBuffer.Length;
                if (remaining > 0)
                    _contentBuffer.Append(content, 0, Math.Min(remaining, content.Length));
                _isTruncated = true;
                _isDirty = true;
                return;
            }

            if (!IsThinking && _contentBuffer.Length > 0 && !EndsWithLineBreak(_contentBuffer))
            {
                _contentBuffer.AppendLine();
            }

            _contentBuffer.Append(content);
            _isDirty = true;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Append();
        }
        else
        {
            Dispatcher.UIThread.Post(Append);
        }
    }

    public void Complete(string status, int? exitCode, long durationMs)
    {
        CompleteCore(status, exitCode, durationMs, forceCollapse: false);
    }

    public void CompleteCollapsed(string status, int? exitCode, long durationMs)
    {
        CompleteCore(status, exitCode, durationMs, forceCollapse: true);
    }

    private void CompleteCore(string status, int? exitCode, long durationMs, bool forceCollapse)
    {
        void Finish()
        {
            UpdateHeader(status, exitCode, durationMs);
            FlushContent();
            _flushTimer.Stop();

            if (forceCollapse || !_manualToggled)
            {
                SetExpanded(false);
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            Finish();
        }
        else
        {
            Dispatcher.UIThread.Post(Finish);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _flushTimer.Stop();
        HeaderButton.Click -= OnHeaderClick;
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void OnHeaderClick(object? sender, RoutedEventArgs e)
    {
        _manualToggled = true;
        SetExpanded(!_isExpanded);
    }

    private void SetExpanded(bool expanded)
    {
        _isExpanded = expanded;
        if (_arrowImage?.RenderTransform is RotateTransform rotateTransform)
        {
            rotateTransform.Angle = expanded ? 180 : 90;
        }

        DetailPanel.IsVisible = expanded;
    }

    private void UpdateHeader(string status, int? exitCode, long durationMs)
    {
        _status = string.IsNullOrWhiteSpace(status) ? "running" : status.Trim().ToLowerInvariant();
        _exitCode = exitCode;
        _durationMs = durationMs > 0 ? durationMs : _stopwatch.ElapsedMilliseconds;

        TitleBlock.Text = Title;
        IconBlock.Text = Kind switch
        {
            "command" => "▣",
            "thinking" => "✦",
            "agent" => "◇",
            _ => "⚙",
        };

        MetaBlock.Text = BuildMetaText();
        ApplyStatusBrush();
    }

    private string BuildMetaText()
    {
        var statusText = _status switch
        {
            "success" => "成功",
            "failed" => "失败",
            "cancelled" => "已取消",
            _ => "运行中",
        };

        var durationText = _durationMs > 0 ? $" · {FormatDuration(_durationMs)}" : string.Empty;
        var exitText = _exitCode.HasValue ? $" · 退出码 {_exitCode.Value}" : string.Empty;
        return $"{statusText}{durationText}{exitText}";
    }

    private void FlushContent()
    {
        if (!_isDirty)
        {
            return;
        }

        var text = _contentBuffer.ToString();
        if (_isTruncated)
            text += "\n\n--- 输出已截断（超过 256 KB），完整内容已记录在执行结果中 ---";

        ContentBlock.Text = text;
        DetailScroller.ScrollToEnd();
        _isDirty = false;
    }

    private void ApplyTextBrushes()
    {
        if (TryFindBrush("SubtextBrush") is IBrush subtextBrush)
        {
            TitleBlock.Foreground = subtextBrush;
            IconBlock.Foreground = subtextBrush;
            ContentBlock.Foreground = subtextBrush;
            MetaBlock.Foreground = subtextBrush;
        }
    }

    private void ApplyStatusBrush()
    {
        IBrush? brush = _status switch
        {
            "success" => TryFindBrush("GreenBrush"),
            "failed" => TryFindBrush("RedBrush"),
            "cancelled" => TryFindBrush("SubtextBrush"),
            _ => TryFindBrush("AccentBrush"),
        };

        if (brush is not null)
        {
            IconBlock.Foreground = brush;
        }
    }

    private IBrush? TryFindBrush(string key)
    {
        return this.FindResource(key) is IBrush brush ? brush : null;
    }

    private static bool EndsWithLineBreak(StringBuilder builder)
    {
        if (builder.Length == 0)
        {
            return true;
        }

        var last = builder[^1];
        return last is '\r' or '\n';
    }

    private static string FormatDuration(long durationMs)
    {
        return durationMs >= 1000
            ? $"{durationMs / 1000d:0.0}s"
            : $"{durationMs}ms";
    }
}
