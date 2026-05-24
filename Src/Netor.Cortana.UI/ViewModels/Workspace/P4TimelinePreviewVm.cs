using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Netor.Cortana.UI.ViewModels.Workspace;

/// <summary>
/// P4 时间线 UI 预览 ViewModel。
/// 纯 mock 数据，不依赖任何 DI / App.Services。
/// 构造函数直接生成 25 条时间线事件 + 5 条计划步骤，用于视觉效果验证。
/// </summary>
public sealed class P4TimelinePreviewVm : INotifyPropertyChanged
{
    private bool _isPlanOverviewExpanded;

    public P4TimelinePreviewVm()
    {
        PlanSteps = new ObservableCollection<PlanStepOverviewVm>(BuildMockPlanSteps());
        TimelineEvents = new ObservableCollection<TimelineEventVm>(BuildMockTimeline());
    }

    // ──── 头部信息 ────

    public string TaskTitle => "制作 2026 年 AI 硬件市场分析报告";
    public string StatusText => "已完成";
    public string StatusColor => "#73c991";
    public string DurationText => "12:15";
    public string TotalTokensText => "24.5k tokens";

    // ──── 计划概览面板 ────

    public bool IsPlanOverviewExpanded
    {
        get => _isPlanOverviewExpanded;
        set
        {
            if (SetField(ref _isPlanOverviewExpanded, value))
                OnPropertyChanged(nameof(PlanToggleIcon));
        }
    }

    public string PlanToggleIcon => _isPlanOverviewExpanded ? "▼" : "▶";

    public ObservableCollection<PlanStepOverviewVm> PlanSteps { get; }

    // ──── 时间线事件 ────

    public ObservableCollection<TimelineEventVm> TimelineEvents { get; }

    // ════════════════════════════════════════════════════════════════
    // Mock 数据构建
    // ════════════════════════════════════════════════════════════════

    private static List<PlanStepOverviewVm> BuildMockPlanSteps() =>
    [
        new()
        {
            Sequence = 1,
            Title = "数据采集-芯片市场",
            StatusIcon = "✅",
            StatusColor = "#73c991",
            IsParallel = true,
        },
        new()
        {
            Sequence = 2,
            Title = "数据采集-终端市场",
            StatusIcon = "✅",
            StatusColor = "#73c991",
            IsParallel = true,
        },
        new()
        {
            Sequence = 3,
            Title = "数据分析+趋势研判",
            StatusIcon = "✅",
            StatusColor = "#73c991",
            DependsOnText = "依赖1+2",
        },
        new()
        {
            Sequence = 4,
            Title = "报告撰写+可视化",
            StatusIcon = "✅",
            StatusColor = "#73c991",
            DependsOnText = "依赖3",
        },
        new()
        {
            Sequence = 5,
            Title = "质量审查+交付",
            StatusIcon = "✅",
            StatusColor = "#73c991",
            DependsOnText = "依赖4",
        },
    ];

    private static List<TimelineEventVm> BuildMockTimeline()
    {
        var baseTime = new DateTimeOffset(2026, 5, 24, 1, 15, 0, TimeSpan.FromHours(8));
        DateTimeOffset T(int seconds) => baseTime.AddSeconds(seconds);

        return
        [
            // ── 1. 任务启动 ──
            new()
            {
                EventId = "evt-01",
                Timestamp = T(0),
                EventType = "task_started",
                NodeLevel = "primary",
                Status = "running",
                Title = "任务开始",
                Detail = "制作 2026 年 AI 硬件市场分析报告",
            },

            // ── 2. 阶段 1: 需求分析 ──
            new()
            {
                EventId = "evt-02",
                Timestamp = T(3),
                EventType = "phase_started",
                NodeLevel = "primary",
                Status = "running",
                Title = "阶段 1: 需求分析",
                Detail = "需求分析师正在分析任务需求...",
            },

            // ── 3. 需求分析师提问 ──
            new()
            {
                EventId = "evt-03",
                Timestamp = T(5),
                EventType = "agent_message",
                NodeLevel = "secondary",
                Title = "[需求分析师 → 用户]",
                Detail = "请问关注哪些细分领域？数据来源偏好？报告格式和字数要求？",
            },

            // ── 4. 用户回答 ──
            new()
            {
                EventId = "evt-04",
                Timestamp = T(80),
                EventType = "user_message",
                NodeLevel = "secondary",
                Title = "[用户 → 需求分析师]",
                Detail = "AI芯片和终端设备，公开数据，Markdown格式，5000字",
            },

            // ── 5. 需求分析完成 ──
            new()
            {
                EventId = "evt-05",
                Timestamp = T(85),
                EventType = "phase_completed",
                NodeLevel = "primary",
                Status = "completed",
                Title = "需求分析完成",
                Detail = "要点: AI芯片+终端 | 公开数据 | Markdown | 5000字",
            },

            // ── 6. 阶段 2: 计划制定（对话式） ──
            new()
            {
                EventId = "evt-06",
                Timestamp = T(88),
                EventType = "phase_started",
                NodeLevel = "primary",
                Status = "running",
                Title = "阶段 2: 计划制定",
                Detail = "计划制定师正在根据需求制定执行计划...",
            },

            // ── 6a. 计划制定师展示初步方案 ──
            new()
            {
                EventId = "evt-06a",
                Timestamp = T(100),
                EventType = "agent_message",
                NodeLevel = "secondary",
                Title = "[计划制定师 → 用户]",
                Detail = "根据需求分析，我初步拟定了以下执行计划：\n" +
                         "1. 数据采集-芯片市场（并行）\n" +
                         "2. 数据采集-终端市场（并行）\n" +
                         "3. 数据分析+价格趋势（依赖1+2）\n" +
                         "4. 报告撰写+可视化（依赖3）\n" +
                         "5. 质量审查+交付（依赖4）\n" +
                         "预计耗时 8-12 分钟。您觉得这个方案是否合适？有需要调整的地方吗？",
            },

            // ── 6b. 用户对话式反馈 ──
            new()
            {
                EventId = "evt-06b",
                Timestamp = T(115),
                EventType = "user_message",
                NodeLevel = "secondary",
                Title = "[用户 → 计划制定师]",
                Detail = "步骤3分析完成后，能不能加一个价格预测？另外步骤1和2的数据源能说明一下吗？",
            },

            // ── 6c. 计划制定师回应调整 ──
            new()
            {
                EventId = "evt-06c",
                Timestamp = T(125),
                EventType = "agent_message",
                NodeLevel = "secondary",
                Title = "[计划制定师 → 用户]",
                Detail = "好的，我做了以下调整：\n" +
                         "• 步骤3 增加价格趋势预测子任务\n" +
                         "• 步骤1 数据源：公开财报 + 行业研报 + 新闻\n" +
                         "• 步骤2 数据源：IDC/Gartner 公开数据 + 产品发布会\n" +
                         "调整后预计耗时 10-14 分钟。可以开始执行吗？",
            },

            // ── 7. 用户确认开始执行（对话确认，非按钮） ──
            new()
            {
                EventId = "evt-07",
                Timestamp = T(135),
                EventType = "user_message",
                NodeLevel = "secondary",
                Title = "[用户 → 计划制定师]",
                Detail = "可以，开始执行吧",
            },

            // ── 8. 计划确认，展示最终计划卡片 ──
            new()
            {
                EventId = "evt-08",
                Timestamp = T(136),
                EventType = "plan_confirmed",
                NodeLevel = "primary",
                Status = "completed",
                Title = "计划已确认，准备执行",
                Card = new TimelineCardVm
                {
                    CardType = "plan_confirmation",
                    Title = "📋 最终执行计划",
                    ContentLines =
                    [
                        "步骤1: 数据采集-芯片市场 (并行) — 财报+研报+新闻",
                        "步骤2: 数据采集-终端市场 (并行) — IDC/Gartner+发布会",
                        "步骤3: 数据分析+价格趋势预测 (顺序, 依赖1+2)",
                        "步骤4: 报告撰写+可视化 (顺序, 依赖3)",
                        "步骤5: 质量审查+交付 (顺序, 依赖4)",
                        "",
                        "预计耗时: 10-14 分钟",
                    ],
                    Actions =
                    [
                        new() { Label = "✓ 已确认，执行中", Style = "primary", ActionId = "confirmed" },
                    ],
                },
            },

            // ── 9. 阶段 3: 执行 ──
            new()
            {
                EventId = "evt-09",
                Timestamp = T(136),
                EventType = "phase_started",
                NodeLevel = "primary",
                Status = "running",
                Title = "阶段 3: 开始执行",
            },

            // ── 10. 步骤 1 开始（含子智能体创建审批） ──
            new()
            {
                EventId = "evt-10",
                Timestamp = T(137),
                EventType = "step_started",
                NodeLevel = "primary",
                Status = "running",
                StepId = "step-1",
                StepSequence = 1,
                Title = "步骤 1: 数据采集-芯片市场 ▶ 开始",
                Detail = "子智能体: 数据采集员-芯片",
                Card = new TimelineCardVm
                {
                    CardType = "agent_creation_auth",
                    Title = "🤖 动态子智能体创建请求",
                    ProposedAgentName = "数据采集员-芯片",
                    ProposedResponsibility = "采集 NVIDIA/AMD/Intel 近 3 年 AI 相关营收数据及产品发布信息",
                    ProposedToolsText = "web_search, web_fetch, read_file",
                    Actions =
                    [
                        new() { Label = "✓ 批准创建", Style = "primary", ActionId = "approve" },
                        new() { Label = "✓✓ 本任务全部批准", Style = "secondary", ActionId = "approve_all" },
                        new() { Label = "✕ 拒绝创建", Style = "danger", ActionId = "reject" },
                    ],
                },
            },

            // ── 11. 步骤 2 开始（并行，含子智能体创建审批） ──
            new()
            {
                EventId = "evt-11",
                Timestamp = T(137),
                EventType = "step_started",
                NodeLevel = "primary",
                Status = "running",
                StepId = "step-2",
                StepSequence = 2,
                Title = "步骤 2: 数据采集-终端市场 ▶ 开始",
                Detail = "子智能体: 数据采集员-终端",
                IsParallel = true,
                Card = new TimelineCardVm
                {
                    CardType = "agent_creation_auth",
                    Title = "🤖 动态子智能体创建请求",
                    ProposedAgentName = "数据采集员-终端",
                    ProposedResponsibility = "采集智能手机/PC/IoT 终端设备出货量与产品动态",
                    ProposedToolsText = "web_search, web_fetch",
                    Actions =
                    [
                        new() { Label = "✓ 批准创建", Style = "primary", ActionId = "approve" },
                        new() { Label = "✓✓ 本任务全部批准", Style = "secondary", ActionId = "approve_all" },
                        new() { Label = "✕ 拒绝创建", Style = "danger", ActionId = "reject" },
                    ],
                },
            },

            // ── 12. 步骤 1 进度 ──
            new()
            {
                EventId = "evt-12",
                Timestamp = T(210),
                EventType = "step_progress",
                NodeLevel = "secondary",
                Status = "running",
                StepId = "step-1",
                StepSequence = 1,
                Title = "步骤 1 进度",
                Detail = "采集到 30/47 条记录",
                ProgressPercent = 64,
            },

            // ── 13. 步骤 2 完成 ──
            new()
            {
                EventId = "evt-13",
                Timestamp = T(242),
                EventType = "step_completed",
                NodeLevel = "primary",
                Status = "completed",
                StepId = "step-2",
                StepSequence = 2,
                Title = "步骤 2: 数据采集-终端市场 ✅ 完成 (1:45)",
                Detail = "结果: 已采集 23 条产品动态信息",
            },

            // ── 14. 步骤 1 完成 ──
            new()
            {
                EventId = "evt-14",
                Timestamp = T(285),
                EventType = "step_completed",
                NodeLevel = "primary",
                Status = "completed",
                StepId = "step-1",
                StepSequence = 1,
                Title = "步骤 1: 数据采集-芯片市场 ✅ 完成 (2:28)",
                Detail = "结果: 已采集 47 条数据，覆盖 NVIDIA/AMD/Intel",
            },

            // ── 15. 步骤 3 开始 ──
            new()
            {
                EventId = "evt-15",
                Timestamp = T(286),
                EventType = "step_started",
                NodeLevel = "primary",
                Status = "running",
                StepId = "step-3",
                StepSequence = 3,
                Title = "步骤 3: 数据分析 ▶ 开始",
                Detail = "子智能体: 数据分析师",
            },

            // ── 16. 步骤 3 进度 ──
            new()
            {
                EventId = "evt-16",
                Timestamp = T(310),
                EventType = "step_progress",
                NodeLevel = "secondary",
                Status = "running",
                StepId = "step-3",
                StepSequence = 3,
                Title = "步骤 3 进度",
                Detail = "市场规模分析完成，正在分析竞争格局...",
                ProgressPercent = 45,
            },

            // ── 17. 步骤 3 失败 ──
            new()
            {
                EventId = "evt-17",
                Timestamp = T(390),
                EventType = "step_failed",
                NodeLevel = "primary",
                Status = "failed",
                StepId = "step-3",
                StepSequence = 3,
                Title = "步骤 3 ⚠ 执行失败: API 连接超时",
                Detail = "→ 自动重试 (1/3)，等待 2s...",
            },

            // ── 18. 步骤 3 重试 ──
            new()
            {
                EventId = "evt-18",
                Timestamp = T(395),
                EventType = "step_retrying",
                NodeLevel = "secondary",
                Status = "retrying",
                StepId = "step-3",
                StepSequence = 3,
                Title = "步骤 3 重试中...",
                Detail = "重试尝试 1/3",
            },

            // ── 19. 步骤 3 重试成功 ──
            new()
            {
                EventId = "evt-19",
                Timestamp = T(420),
                EventType = "step_progress",
                NodeLevel = "secondary",
                Status = "running",
                StepId = "step-3",
                StepSequence = 3,
                Title = "步骤 3 重试成功，继续执行",
            },

            // ── 20. 步骤 3 完成 ──
            new()
            {
                EventId = "evt-20",
                Timestamp = T(570),
                EventType = "step_completed",
                NodeLevel = "primary",
                Status = "completed",
                StepId = "step-3",
                StepSequence = 3,
                Title = "步骤 3: 数据分析 ✅ 完成 (4:44)",
                Detail = "结果: 识别 3 个核心趋势，竞争格局清晰",
            },

            // ── 21. 步骤 4 开始 ──
            new()
            {
                EventId = "evt-21",
                Timestamp = T(571),
                EventType = "step_started",
                NodeLevel = "primary",
                Status = "running",
                StepId = "step-4",
                StepSequence = 4,
                Title = "步骤 4: 报告撰写 ▶ 开始",
                Detail = "子智能体: 报告撰写员",
            },

            // ── 22. 工具调用授权卡片 ──
            new()
            {
                EventId = "evt-22",
                Timestamp = T(600),
                EventType = "waiting_user",
                NodeLevel = "primary",
                Status = "waiting",
                StepId = "step-4",
                StepSequence = 4,
                Title = "⚠️ 工具调用需要确认",
                Card = new TimelineCardVm
                {
                    CardType = "tool_call_auth",
                    Title = "⚠️ 工具调用需要确认",
                    ToolName = "publish_to_bilibili",
                    CallerAgentName = "报告撰写员",
                    ToolParameters = new Dictionary<string, string>
                    {
                        ["title"] = "2026 AI 芯片市场分析",
                        ["video_path"] = "workspace/output.mp4",
                        ["description"] = "基于 NVIDIA/AMD/Intel 最新财报数据...",
                    },
                    RiskDescription = "此操作会发布内容到公开平台，发布后不可自动撤回。",
                    Actions =
                    [
                        new() { Label = "✓ 允许", Style = "primary", ActionId = "allow_once" },
                        new() { Label = "✓ 后续全部允许", Style = "secondary", ActionId = "allow_all" },
                        new() { Label = "✗ 拒绝", Style = "danger", ActionId = "deny" },
                    ],
                },
            },

            // ── 23. 步骤 4 完成 ──
            new()
            {
                EventId = "evt-23",
                Timestamp = T(705),
                EventType = "step_completed",
                NodeLevel = "primary",
                Status = "completed",
                StepId = "step-4",
                StepSequence = 4,
                Title = "步骤 4: 报告撰写 ✅ 完成 (2:14)",
                Detail = "结果: 报告已生成，含可视化图表",
            },

            // ── 24. 步骤 5 开始 ──
            new()
            {
                EventId = "evt-24",
                Timestamp = T(706),
                EventType = "step_started",
                NodeLevel = "primary",
                Status = "running",
                StepId = "step-5",
                StepSequence = 5,
                Title = "步骤 5: 质量审查 ▶ 开始",
                Detail = "子智能体: 质量审查员",
            },

            // ── 25. 任务完成 → 执行报表 ──
            new()
            {
                EventId = "evt-25",
                Timestamp = T(735),
                EventType = "task_completed",
                NodeLevel = "primary",
                Status = "completed",
                Title = "全部步骤完成 ✅",
                Card = new TimelineCardVm
                {
                    CardType = "execution_report",
                    Title = "📊 执行报表",
                    ContentLines =
                    [
                        "总步骤: 5  |  耗时: 12:15  |  Token: 24.5k",
                        "子智能体: 7 个  |  重试: 1 次  |  工具授权: 1 次",
                        "交付物: reports/AI-市场分析-2026.md",
                    ],
                    Actions =
                    [
                        new() { Label = "📄 查看报告", Style = "primary", ActionId = "view_report" },
                        new() { Label = "📋 保存为模板", Style = "secondary", ActionId = "save_template" },
                        new() { Label = "🔄 新建任务", Style = "secondary", ActionId = "new_task" },
                    ],
                },
            },
        ];
    }

    // ──── INotifyPropertyChanged ────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
