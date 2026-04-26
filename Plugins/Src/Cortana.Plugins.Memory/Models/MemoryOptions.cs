namespace Cortana.Plugins.Memory.Models;

/// <summary>
/// 记忆系统运行配置集合。
/// </summary>
public sealed class MemoryOptions
{
    /// <summary>
    /// 记忆衰减配置。
    /// </summary>
    public MemoryDecayOptions Decay { get; init; } = new();

    /// <summary>
    /// 记忆保留配置。
    /// </summary>
    public MemoryRetentionOptions Retention { get; init; } = new();

    /// <summary>
    /// 记忆召回配置。
    /// </summary>
    public MemoryRecallOptions Recall { get; init; } = new();

    /// <summary>
    /// 主动记忆供应配置。
    /// </summary>
    public MemorySupplyOptions Supply { get; init; } = new();

    /// <summary>
    /// 抽象记忆生成配置。
    /// </summary>
    public MemoryAbstractionOptions Abstraction { get; init; } = new();

    /// <summary>
    /// 治理审计配置。
    /// </summary>
    public MemoryGovernanceOptions Governance { get; init; } = new();
}

/// <summary>
/// 记忆衰减配置。
/// </summary>
public sealed class MemoryDecayOptions
{
    /// <summary>
    /// 是否启用记忆衰减。
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// 默认记忆衰减率。
    /// </summary>
    public double DefaultRate { get; init; } = 0.015;

    /// <summary>
    /// 衰减扫描间隔，单位分钟。
    /// </summary>
    public int ScanIntervalMinutes { get; init; } = 60;
}

/// <summary>
/// 记忆保留配置。
/// </summary>
public sealed class MemoryRetentionOptions
{
    /// <summary>
    /// 参与保留计算的最低保留分数。
    /// </summary>
    public double MinimumScore { get; init; } = 0.2;

    /// <summary>
    /// 低于该阈值的记忆可进入遗忘候选。
    /// </summary>
    public double ForgetThreshold { get; init; } = 0.05;
}

/// <summary>
/// 记忆召回配置。
/// </summary>
public sealed class MemoryRecallOptions
{
    /// <summary>
    /// 每次向上层提供的最大记忆窗口数量。
    /// </summary>
    public int MaxWindowCount { get; init; } = 6;

    /// <summary>
    /// 每次召回最多返回的记忆数量。
    /// </summary>
    public int MaxMemoryCount { get; init; } = 20;

    /// <summary>
    /// 允许参与召回的最低记忆可信度。
    /// </summary>
    public double MinimumConfidence { get; init; } = 0.35;

    /// <summary>
    /// 是否允许候选记忆参与召回。
    /// </summary>
    public bool IncludeCandidateMemories { get; init; }
}

/// <summary>
/// 主动记忆供应配置。
/// </summary>
public sealed class MemorySupplyOptions
{
    /// <summary>
    /// 是否启用主动记忆供应。
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// 主动供应给上层的最大记忆数量。
    /// </summary>
    public int MaxMemoryCount { get; init; } = 8;
}

/// <summary>
/// 抽象记忆生成配置。
/// </summary>
public sealed class MemoryAbstractionOptions
{
    /// <summary>
    /// 是否启用自动抽象记忆生成。
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// 生成抽象记忆需要的最小支撑记忆数。
    /// </summary>
    public int MinimumSupportCount { get; init; } = 3;

    /// <summary>
    /// 抽象记忆入库所需的最低可信度。
    /// </summary>
    public double MinimumConfidence { get; init; } = 0.55;
}

/// <summary>
/// 记忆治理审计配置。
/// </summary>
public sealed class MemoryGovernanceOptions
{
    /// <summary>
    /// 是否启用记忆治理审计。
    /// </summary>
    public bool AuditEnabled { get; init; } = true;
}
