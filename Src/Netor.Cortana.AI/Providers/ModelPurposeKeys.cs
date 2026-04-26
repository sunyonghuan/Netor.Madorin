namespace Netor.Cortana.AI.Providers;

/// <summary>
/// 统一维护用途级模型配置键，避免魔法字符串分散。
/// </summary>
public static class ModelPurposeKeys
{
    /// <summary>对话压缩/摘要模型 Setting 键。</summary>
    public const string CompactionModelId = "Compaction.ModelId";

    /// <summary>长期记忆候选抽取/加工模型 Setting 键。</summary>
    public const string MemoryProcessingModelId = "Memory.ModelId";

    /// <summary>长期记忆抽象/归纳模型 Setting 键。</summary>
    public const string MemoryAbstractionModelId = "Memory.AbstractionModelId";

    /// <summary>记忆冲突裁决/一致性校验模型 Setting 键。</summary>
    public const string MemoryConflictResolutionModelId = "Memory.ConflictModelId";
}
