using Cortana.Plugins.Memory.Models;

namespace Cortana.Plugins.Memory.Processing;

/// <summary>
/// 记忆语义处理器，用于隔离宿主授权的大模型能力。
/// </summary>
public interface IMemorySemanticProcessor
{
    /// <summary>
    /// 从观察记录中生成候选长期记忆。
    /// </summary>
    IReadOnlyList<MemorySemanticCandidate> ExtractCandidates(ObservationRecord observation, string traceId);
}
