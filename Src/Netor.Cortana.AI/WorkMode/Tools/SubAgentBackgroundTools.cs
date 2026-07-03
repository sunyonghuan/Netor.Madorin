using System.ComponentModel;
using System.Text.Json;

using Microsoft.Extensions.AI;

using Netor.Cortana.AI.WorkMode.Background;
using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI.WorkMode.Tools;

/// <summary>
/// 子智能体背景执行工具。
/// </summary>
public sealed class SubAgentBackgroundTools
{
    private readonly IBackgroundJobExecutor _executor;
    private readonly string _taskId;
    private readonly AiProviderEntity _mainProvider;
    private readonly AiModelEntity _mainModel;

    public SubAgentBackgroundTools(
        IBackgroundJobExecutor executor,
        string taskId,
        AiProviderEntity mainProvider,
        AiModelEntity mainModel)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _taskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
        _mainProvider = mainProvider ?? throw new ArgumentNullException(nameof(mainProvider));
        _mainModel = mainModel ?? throw new ArgumentNullException(nameof(mainModel));
    }

    public AIFunction CreateStartSubAgentTool(AgentEntity agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        [Description("启动一个子智能体背景任务。适合耗时较长、可以稍后 wait 的工作。返回 jobId。")]
        async Task<string> StartSubAgentAsync(
            [Description("交给子智能体处理的具体任务说明")] string query,
            [Description("附件绝对路径列表，可空。用 JSON 数组字符串或逗号/换行分隔文本表示")] string? attachmentPaths,
            [Description("每个附件的简短描述，可空，长度应与 attachmentPaths 一致。用 JSON 数组字符串或逗号/换行分隔文本表示")] string? attachmentDescriptions,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return "错误：query 不能为空";
            }

            var parsedAttachmentPaths = ParseStringListArgument(attachmentPaths);
            var parsedAttachmentDescriptions = ParseStringListArgument(attachmentDescriptions);

            var input = new SubAgentJobInput(
                agent.Id,
                query.Trim(),
                parsedAttachmentPaths?.ToArray(),
                parsedAttachmentDescriptions?.ToArray(),
                _mainProvider.Id,
                _mainModel.Id);

            var inputJson = JsonSerializer.Serialize(input, WorkModeJsonContext.Default.SubAgentJobInput);
            var jobId = await _executor.StartAsync(
                new BackgroundJobStartRequest(_taskId, inputJson, agent.Name),
                ct);

            return $"已启动子智能体背景任务：{jobId}";
        }

        return AIFunctionFactory.Create(StartSubAgentAsync, new AIFunctionFactoryOptions
        {
            Name = BuildStartToolName(agent.Id),
            Description = $"[LONG] 启动子智能体「{agent.Name}」在后台执行任务，返回 jobId。"
        });
    }

    public AIFunction CreateWaitForSubAgentTool()
    {
        [Description("等待子智能体背景任务完成，最长 60 秒。未完成时返回当前进度。")]
        async Task<string> WaitForSubAgentAsync(
            [Description("start_subagent_* 返回的 jobId")] string jobId,
            [Description("最多等待秒数，上限 60")] int maxSeconds,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return "错误：jobId 不能为空";
            }

            var status = await _executor.WaitForAsync(jobId.Trim(), maxSeconds, ct);
            return JsonSerializer.Serialize(status, WorkModeJsonContext.Default.BackgroundJobStatus);
        }

        return AIFunctionFactory.Create(WaitForSubAgentAsync, new AIFunctionFactoryOptions
        {
            Name = "wait_for_subagent",
            Description = "[LONG] 等待子智能体背景任务完成或返回当前进度。"
        });
    }

    public AIFunction CreateCancelSubAgentTool()
    {
        [Description("取消子智能体背景任务。")]
        async Task<string> CancelSubAgentAsync(
            [Description("start_subagent_* 返回的 jobId")] string jobId,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return "错误：jobId 不能为空";
            }

            await _executor.CancelAsync(jobId.Trim(), ct);
            return $"已取消子智能体背景任务：{jobId.Trim()}";
        }

        return AIFunctionFactory.Create(CancelSubAgentAsync, new AIFunctionFactoryOptions
        {
            Name = "cancel_subagent",
            Description = "[LONG] 取消子智能体背景任务。"
        });
    }

    private static string BuildStartToolName(string agentId)
    {
        var safeIdPart = new string([.. (agentId ?? string.Empty)
            .Where(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
            .Take(8)]);
        if (string.IsNullOrEmpty(safeIdPart))
        {
            safeIdPart = "unknown";
        }

        return $"start_subagent_{safeIdPart}";
    }

    private static List<string>? ParseStringListArgument(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return document.RootElement
                        .EnumerateArray()
                        .Where(static item => item.ValueKind == JsonValueKind.String)
                        .Select(static item => item.GetString()?.Trim())
                        .Where(static item => !string.IsNullOrWhiteSpace(item))
                        .Select(static item => item!)
                        .ToList();
                }
            }
            catch (JsonException)
            {
                // 回退到分隔符解析。
            }
        }

        return text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }
}
