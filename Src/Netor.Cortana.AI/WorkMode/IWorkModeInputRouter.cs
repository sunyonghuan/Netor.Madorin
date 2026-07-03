namespace Netor.Cortana.AI.WorkMode;

/// <summary>
/// 工作模式输入路由器。
/// 根据用户输入和当前状态，决定如何处理输入（新建任务 / 继续任务 / 软抢占等）。
/// 详见 Docs/已完成功能规划/工作模式方案策划/12-输入路由与状态机设计.md。
/// </summary>
public interface IWorkModeInputRouter
{
    /// <summary>
    /// 路由用户输入到对应的处理逻辑。
    /// </summary>
    /// <param name="input">用户输入文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RouteAsync(string input, CancellationToken cancellationToken = default);
}
