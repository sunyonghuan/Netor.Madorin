using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Reliability;

namespace Netor.Cortana.AI.WorkMode.Tools;

/// <summary>
/// 工具重试包装器，为 AIFunction 添加自动重试能力。
/// 详见 Docs/已完成功能规划/工作模式方案策划/16-长任务可靠性与重试策略.md §5.3。
/// </summary>
public sealed class RetryingFunctionWrapper
{
    private readonly ILogger? _logger;

    public RetryingFunctionWrapper(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// 包装 AIFunction，添加重试逻辑。
    /// v1.0 阶段 1 骨架：直接返回原始函数，阶段 2 完善时改为真正的包装。
    /// </summary>
    /// <param name="function">原始工具函数。</param>
    /// <returns>带重试能力的工具函数。</returns>
    public AIFunction Wrap(AIFunction function)
    {
        // TODO: 阶段 2 完善 - 使用 DelegatingAIFunction 或自定义包装实现重试
        return function;
    }

    /// <summary>
    /// 批量包装工具列表。
    /// </summary>
    /// <param name="functions">原始工具列表。</param>
    /// <returns>带重试能力的工具列表。</returns>
    public IReadOnlyList<AIFunction> WrapAll(IReadOnlyList<AIFunction> functions)
    {
        var wrapped = new List<AIFunction>(functions.Count);
        foreach (var f in functions)
            wrapped.Add(Wrap(f));
        return wrapped;
    }

    /// <summary>
    /// 带重试的工具调用（供 WorkflowExecutor 在工具调用层面使用）。
    /// </summary>
    public async Task<object?> InvokeWithRetryAsync(AIFunction function, CancellationToken cancellationToken)
    {
        var attempt = 0;

        while (true)
        {
            try
            {
                return await function.InvokeAsync(cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                attempt++;
                var (category, maxRetries) = FailureClassifier.Classify(ex, ex.Message);

                if (attempt >= maxRetries || maxRetries == 0)
                {
                    _logger?.LogWarning(ex,
                        "工具 {ToolName} 第 {Attempt} 次调用失败（{Category}），已达最大重试次数 {Max}",
                        function.Name, attempt, category, maxRetries);
                    throw;
                }

                _logger?.LogInformation(
                    "工具 {ToolName} 第 {Attempt} 次调用失败（{Category}），将重试（最多 {Max} 次）",
                    function.Name, attempt, category, maxRetries);

                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                await Task.Delay(delay, cancellationToken);
            }
        }
    }
}
