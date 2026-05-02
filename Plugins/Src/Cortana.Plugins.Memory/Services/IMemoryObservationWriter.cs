using Cortana.Plugins.Memory.Models;

namespace Cortana.Plugins.Memory.Services;

/// <summary>
/// 将外部输入标准化为 observation 记录。
/// </summary>
public interface IMemoryObservationWriter
{
    /// <summary>
    /// 记录一条对话消息并返回写入结果。
    /// </summary>
    /// <param name="request">待记录的对话消息。</param>
    /// <returns>写入结果。</returns>
    MemoryRecordTurnResult RecordTurn(MemoryRecordTurnRequest request);
}
