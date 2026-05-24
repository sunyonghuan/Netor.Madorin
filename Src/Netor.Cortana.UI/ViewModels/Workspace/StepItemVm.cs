using System.ComponentModel;
using System.Runtime.CompilerServices;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.UI.ViewModels.Workspace;

/// <summary>
/// P4 重写：单条任务步骤的 ViewModel。
/// 实现 <see cref="INotifyPropertyChanged"/>，由 P4 事件流增量追加
/// （<see cref="Events.OnTaskStepStarted"/> / <see cref="Events.OnTaskStepCompleted"/>）。
///
/// 不订阅事件，仅作为数据载体。
/// </summary>
public class StepItemVm : INotifyPropertyChanged
{
    private string _status = string.Empty;
    private long? _completedAt;
    private long? _durationMs;
    private string? _summary;

    /// <summary>P4 构造：从 TaskStepEventArgs 构造步骤项。</summary>
    public StepItemVm(TaskStepEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        StepId = args.StepId;
        Sequence = args.StepSequence;
        AgentId = string.Empty;
        AgentName = string.Empty;
        Action = args.Title;
        StartedAt = args.OccurredAt.ToUnixTimeMilliseconds();
        _status = args.Status;
        _completedAt = null;
        _durationMs = null;
        _summary = args.ResultSummary;
    }

    /// <summary>从老 step.completed 事件构造（兼容历史数据加载）。</summary>
    public StepItemVm(WorkflowStepCompletedArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        StepId = args.StepId;
        Sequence = args.Sequence;
        AgentId = args.AgentId ?? string.Empty;
        AgentName = args.AgentName ?? args.AgentId ?? "(未知)";
        Action = args.Action;
        StartedAt = args.StartedAt;
        _status = args.Status;
        _completedAt = args.CompletedAt;
        _durationMs = args.DurationMs;
        _summary = args.SummaryJson;
    }

    /// <summary>步骤主键。</summary>
    public string StepId { get; }

    /// <summary>任务内序号。</summary>
    public int Sequence { get; }

    /// <summary>执行步骤的 Agent ID。</summary>
    public string AgentId { get; }

    /// <summary>显示名称。</summary>
    public string AgentName { get; }

    /// <summary>动作类型（speak / plan / execute / ...）。</summary>
    public string Action { get; }

    /// <summary>启动时间（Unix ms）。</summary>
    public long StartedAt { get; }

    /// <summary>状态文本（running / completed / failed / skipped）。</summary>
    public string Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusIcon));
                OnPropertyChanged(nameof(StatusColorHex));
            }
        }
    }

    /// <summary>完成时间（Unix ms）。</summary>
    public long? CompletedAt
    {
        get => _completedAt;
        set => SetField(ref _completedAt, value);
    }

    /// <summary>耗时（毫秒）。</summary>
    public long? DurationMs
    {
        get => _durationMs;
        set
        {
            if (SetField(ref _durationMs, value))
                OnPropertyChanged(nameof(DurationText));
        }
    }

    /// <summary>步骤摘要（OutputContent / 备注）。</summary>
    public string? Summary
    {
        get => _summary;
        set => SetField(ref _summary, value);
    }

    // ──── 派生显示属性 ────

    public string StatusIcon => _status switch
    {
        "running" => "●",
        "completed" => "✓",
        "failed" => "✕",
        "skipped" => "⊘",
        _ => "?",
    };

    public string StatusColorHex => _status switch
    {
        "running" => "#3794ff",
        "completed" => "#73c991",
        "failed" => "#f48771",
        "skipped" => "#858585",
        _ => "#858585",
    };

    public string DurationText
    {
        get
        {
            if (_durationMs is null or 0) return string.Empty;
            var seconds = _durationMs.Value / 1000.0;
            return seconds < 60 ? $"{seconds:F1}s" : $"{seconds / 60:F1}m";
        }
    }

    // ──── INotifyPropertyChanged ────

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
