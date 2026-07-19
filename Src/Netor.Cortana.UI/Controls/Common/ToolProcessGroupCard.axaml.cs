using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Netor.Cortana.UI.Controls.Common;

public sealed partial class ToolProcessGroupCard : UserControl
{
    private readonly Dictionary<string, RealtimeProcessCardHandle> _handles = new(StringComparer.Ordinal);
    private readonly Image? _arrowImage;

    private bool _isExpanded = true;
    private bool _manualToggled;

    public ToolProcessGroupCard()
        : this(Guid.NewGuid().ToString("N"))
    {
    }

    public ToolProcessGroupCard(string groupId)
    {
        InitializeComponent();

        GroupId = string.IsNullOrWhiteSpace(groupId) ? Guid.NewGuid().ToString("N") : groupId;
        _arrowImage = this.FindControl<Image>("ArrowImage");

        HeaderButton.Cursor = new Cursor(StandardCursorType.Hand);
        HeaderButton.Click += OnHeaderClick;

        ApplyTextBrushes();
        SetExpanded(false);
        RefreshSummary();
    }

    public string GroupId { get; }

    public RealtimeProcessCardHandle AddTool(RealtimeProcessEvent initial)
    {
        if (_handles.TryGetValue(initial.ProcessId, out var existing))
        {
            existing.UpdateStatus(initial.Status, initial.ExitCode, initial.DurationMs);
            existing.AppendContent(initial.Content);
            RefreshSummary();
            return existing;
        }

        var card = new RealtimeProcessCard(initial);
        NormalizeNestedCardMargin(card);

        var handle = new RealtimeProcessCardHandle(card);
        _handles[handle.ProcessId] = handle;
        ToolList.Children.Add(card);
        RefreshSummary();
        return handle;
    }

    public bool TryGetTool(string processId, out RealtimeProcessCardHandle handle)
    {
        return _handles.TryGetValue(processId, out handle!);
    }

    public void UpdateTool(RealtimeProcessEvent evt)
    {
        if (!_handles.TryGetValue(evt.ProcessId, out var handle))
        {
            handle = AddTool(new RealtimeProcessEvent
            {
                TurnId = evt.TurnId,
                ProcessId = evt.ProcessId,
                Kind = evt.Kind,
                Title = evt.Title,
                Status = "running",
                Content = string.Empty,
                ExitCode = evt.ExitCode,
                DurationMs = evt.DurationMs,
                Timestamp = evt.Timestamp,
            });
        }

        var status = NormalizeStatus(evt.Status);
        handle.AppendContent(evt.Content);
        if (status == "running")
        {
            handle.UpdateStatus(status, evt.ExitCode, evt.DurationMs);
        }
        else
        {
            handle.Complete(status, evt.ExitCode, evt.DurationMs);
        }

        RefreshSummary();
        ApplyAutoCollapse();
    }

    public void CancelRunningTools(string reason)
    {
        foreach (var handle in _handles.Values.Where(static h => IsRunningStatus(h.Status)).ToList())
        {
            if (!string.IsNullOrWhiteSpace(reason))
            {
                handle.AppendContent(reason);
            }

            handle.Complete("cancelled", null, handle.DurationMs);
        }

        RefreshSummary();
    }

    public void CompleteIfIdle()
    {
        RefreshSummary();
        ApplyAutoCollapse();
    }

    private void OnHeaderClick(object? sender, RoutedEventArgs e)
    {
        _manualToggled = true;
        SetExpanded(!_isExpanded);
    }

    private void SetExpanded(bool expanded)
    {
        _isExpanded = expanded;
        BodyPanel.IsVisible = expanded;

        if (_arrowImage?.RenderTransform is RotateTransform rotateTransform)
        {
            rotateTransform.Angle = expanded ? 180 : 90;
        }
    }

    private void ApplyAutoCollapse()
    {
        var snapshot = BuildSnapshot();
        if (snapshot.Running > 0)
        {
            return;
        }

        if (snapshot.Failed > 0 || snapshot.Cancelled > 0)
        {
            if (!_manualToggled)
            {
                SetExpanded(true);
            }

            return;
        }

        if (!_manualToggled)
        {
            SetExpanded(false);
        }
    }

    private void RefreshSummary()
    {
        var snapshot = BuildSnapshot();
        TitleBlock.Text = $"工具调用 {snapshot.Total} 个";
        SummaryBlock.Text = BuildSummary(snapshot);

        if (TryFindBrush(snapshot.Failed > 0 ? "RedBrush" : snapshot.Cancelled > 0 ? "SubtextBrush" : snapshot.Running > 0 ? "AccentBrush" : "GreenBrush") is { } brush)
        {
            IconBlock.Foreground = brush;
        }
    }

    private ToolSummarySnapshot BuildSnapshot()
    {
        var total = _handles.Count;
        var running = 0;
        var success = 0;
        var failed = 0;
        var cancelled = 0;
        long totalDurationMs = 0;

        foreach (var handle in _handles.Values)
        {
            totalDurationMs += Math.Max(0, handle.DurationMs);
            switch (NormalizeStatus(handle.Status))
            {
                case "success":
                    success++;
                    break;
                case "failed":
                    failed++;
                    break;
                case "cancelled":
                    cancelled++;
                    break;
                default:
                    running++;
                    break;
            }
        }

        return new ToolSummarySnapshot(total, running, success, failed, cancelled, totalDurationMs);
    }

    private static string BuildSummary(ToolSummarySnapshot snapshot)
    {
        if (snapshot.Total == 0)
        {
            return "等待中";
        }

        var parts = new List<string>();
        if (snapshot.Running > 0)
        {
            parts.Add($"运行中 {snapshot.Running}");
        }

        if (snapshot.Success > 0)
        {
            parts.Add($"成功 {snapshot.Success}");
        }

        if (snapshot.Failed > 0)
        {
            parts.Add($"失败 {snapshot.Failed}");
        }

        if (snapshot.Cancelled > 0)
        {
            parts.Add($"取消 {snapshot.Cancelled}");
        }

        if (snapshot.TotalDurationMs > 0)
        {
            parts.Add(FormatDuration(snapshot.TotalDurationMs));
        }

        return string.Join(" · ", parts);
    }

    private void ApplyTextBrushes()
    {
        if (TryFindBrush("SubtextBrush") is not { } subtextBrush)
        {
            return;
        }

        TitleBlock.Foreground = subtextBrush;
        SummaryBlock.Foreground = subtextBrush;
        IconBlock.Foreground = subtextBrush;
    }

    private IBrush? TryFindBrush(string key)
    {
        return this.FindResource(key) is IBrush brush ? brush : null;
    }

    private static void NormalizeNestedCardMargin(RealtimeProcessCard card)
    {
        card.Margin = new Thickness(0);
        if (card.FindControl<Border>("RootBorder") is { } rootBorder)
        {
            rootBorder.Margin = new Thickness(0);
        }
    }

    private static bool IsRunningStatus(string status)
    {
        return NormalizeStatus(status) == "running";
    }

    private static string NormalizeStatus(string status)
    {
        return string.IsNullOrWhiteSpace(status) ? "running" : status.Trim().ToLowerInvariant();
    }

    private static string FormatDuration(long durationMs)
    {
        return durationMs >= 1000
            ? $"{durationMs / 1000d:0.0}s"
            : $"{durationMs}ms";
    }

    private sealed record ToolSummarySnapshot(
        int Total,
        int Running,
        int Success,
        int Failed,
        int Cancelled,
        long TotalDurationMs);
}
