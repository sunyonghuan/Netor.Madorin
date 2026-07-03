namespace Netor.Cortana.AI.WorkMode.Prompts;

/// <summary>
/// 提示词提供者接口。
/// 工作模式的提示词可从多个来源加载（本地文件、数据库、远程等）。
/// 详见 Docs/已完成功能规划/工作模式方案策划/11-AF框架集成方案.md §2.6。
/// </summary>
public interface IPromptProvider
{
    /// <summary>
    /// 获取指定名称的提示词内容。
    /// </summary>
    /// <param name="name">提示词名称（如 "work_mode.general_manager"）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>提示词内容；未找到时返回 null。</returns>
    Task<string?> GetPromptAsync(string name, CancellationToken cancellationToken = default);
}
