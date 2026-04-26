using Cortana.Plugins.Memory.Models;

namespace Cortana.Plugins.Memory.Services;

/// <summary>
/// 提供人工记忆写入能力。
/// </summary>
public interface IMemoryNoteService
{
    /// <summary>
    /// 写入一条用户明确授权的人工记忆。
    /// </summary>
    /// <param name="request">人工记忆写入请求。</param>
    /// <returns>人工记忆写入结果。</returns>
    MemoryAddNoteResult AddNote(MemoryAddNoteRequest request);
}
