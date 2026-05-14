using System.Diagnostics;
using System.Text;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.UI.Controls;

public partial class RealtimeProcessCard : UserControl, IDisposable
{
    private readonly StringBuilder _contentBuffer = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    private bool _isDirty;
    private bool _isExpanded = true;
    private bool _manualToggled;
    private bool _disposed;
    private string _status = "running";
    private int? _exitCode;
    private long _durationMs;

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

        Dispatcher.UIThread.Post(() =>
        {
            if (_contentBuffer.Length > 0 && !EndsWithLineBreak(_contentBuffer))
            {
                _contentBuffer.AppendLine();
            }

            _contentBuffer.Append(content);
            _isDirty = true;
        });
    }

    public void Complete(string status, int? exitCode, long durationMs)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateHeader(status, exitCode, durationMs);
            FlushContent();
            _flushTimer.Stop();

            if (!_manualToggled)
            {
                SetExpanded(false);
            }
        });
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
        ArrowBlock.Text = expanded ? "▾" : "▸";
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

        ContentBlock.Text = _contentBuffer.ToString();
        _isDirty = false;
    }

    private void ApplyTextBrushes()
    {
        if (this.FindResource("TextBrush") is IBrush textBrush)
        {
            TitleBlock.Foreground = textBrush;
            ContentBlock.Foreground = textBrush;
            ArrowBlock.Foreground = textBrush;
            IconBlock.Foreground = textBrush;
        }

        if (this.FindResource("SubtextBrush") is IBrush subtextBrush)
        {
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
            _ => TryFindBrush("TealBrush"),
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
