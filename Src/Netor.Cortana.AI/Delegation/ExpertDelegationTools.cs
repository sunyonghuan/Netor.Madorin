using System.ComponentModel;
using System.Text.Json;

using Microsoft.Extensions.AI;

using Netor.Cortana.AI.Handoff;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.Delegation;

/// <summary>
/// 专家模式后台子智能体编排工具。
/// </summary>
public sealed class ExpertDelegationTools(
    SystemToolCatalogService toolCatalog,
    DelegatedAgentJobExecutor executor)
{
    public IReadOnlyList<AIFunction> CreateTools(
        AgentEntity parentAgent,
        HandoffRuntimeContext runtimeContext)
    {
        ArgumentNullException.ThrowIfNull(parentAgent);
        ArgumentNullException.ThrowIfNull(runtimeContext);

        return
        [
            CreateListSystemToolCatalogTool(),
            CreateStartAutonomousSubAgentTaskTool(parentAgent, runtimeContext),
            CreateGetSubAgentTaskStatusTool(),
            CreateReadSubAgentTaskResultTool(),
            CreateCancelSubAgentTaskTool()
        ];
    }

    private AIFunction CreateListSystemToolCatalogTool()
    {
        [Description("列出系统中可供后台子智能体挂载的工具目录。只读目录，不代表主智能体获得执行权。")]
        async Task<string> ListSystemToolCatalogAsync(CancellationToken ct)
        {
            var tools = await toolCatalog.ListAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(tools, DelegatedAgentJsonContext.Default.ListSystemToolCatalogItem);
        }

        return AIFunctionFactory.Create(ListSystemToolCatalogAsync, new AIFunctionFactoryOptions
        {
            Name = "list_system_tool_catalog",
            Description = "列出后台子智能体可挂载的系统工具目录。主智能体只能查看目录，不能直接执行目录里的工具。"
        });
    }

    private AIFunction CreateStartAutonomousSubAgentTaskTool(
        AgentEntity parentAgent,
        HandoffRuntimeContext runtimeContext)
    {
        [Description("创建一个临时后台子智能体任务，返回 jobId。子智能体会使用 toolMountsJson 中声明的工具执行任务。")]
        async Task<string> StartAutonomousSubAgentTaskAsync(
            [Description("临时子智能体名称")] string childName,
            [Description("临时子智能体系统提示词/角色说明")] string childInstructions,
            [Description("交给临时子智能体完成的具体任务")] string task,
            [Description("必填。子智能体需要挂载的工具名，传 JSON 数组字符串，例如 [\"sys_read_file\"]；工具名必须来自 list_system_tool_catalog。")] string toolMountsJson,
            [Description("可选。AI 厂商 ID；不确定或使用默认配置时可省略。")] string? providerId = null,
            [Description("可选。AI 模型 ID；不确定或使用默认配置时可省略。")] string? modelId = null,
            [Description("可选。附件绝对路径列表，传 JSON 数组字符串；省略时等同 []。")] string? attachmentPathsJson = null,
            [Description("可选。输出格式要求；省略时由子智能体自然输出。")] string? outputContract = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(task))
            {
                return SerializeError("task 不能为空。");
            }

            var toolMounts = ParseStringArray(toolMountsJson);
            if (toolMounts.Count == 0)
            {
                return SerializeError("toolMountsJson 不能为空，必须至少给子智能体挂载一个工具。");
            }

            var missingTools = await toolCatalog.ValidateToolMountsAsync(toolMounts, ct).ConfigureAwait(false);
            if (missingTools.Count > 0)
            {
                return SerializeError($"工具不存在，无法挂载：{string.Join(", ", missingTools)}");
            }

            var attachments = ParseStringArray(attachmentPathsJson);
            var input = new DelegatedAgentTaskInput(
                task.Trim(),
                attachments.ToArray(),
                string.IsNullOrWhiteSpace(outputContract) ? null : outputContract.Trim());

            var job = new DelegatedAgentJobEntity
            {
                Id = $"chatjob_{Guid.NewGuid():N}",
                ScopeKind = "chat",
                ScopeId = runtimeContext.SessionId ?? string.Empty,
                ParentTurnId = string.Empty,
                ParentAgentId = parentAgent.Id,
                ChildName = string.IsNullOrWhiteSpace(childName) ? "delegated-subagent" : childName.Trim(),
                ChildInstructions = string.IsNullOrWhiteSpace(childInstructions)
                    ? "你是一个后台任务子智能体，负责完成主智能体委派的任务。"
                    : childInstructions.Trim(),
                TaskInputJson = JsonSerializer.Serialize(input, DelegatedAgentJsonContext.Default.DelegatedAgentTaskInput),
                ToolMountsJson = JsonSerializer.Serialize(toolMounts.ToArray(), DelegatedAgentJsonContext.Default.StringArray),
                ProviderId = string.IsNullOrWhiteSpace(providerId) ? null : providerId.Trim(),
                ModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim()
            };

            string jobId;
            try
            {
                jobId = await executor.StartAsync(job, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                return SerializeError(ex.Message);
            }

            var result = new DelegatedAgentTaskStartResult(
                jobId,
                DelegatedAgentJobStates.Running,
                job.ChildName,
                "后台子智能体任务已启动。");

            return JsonSerializer.Serialize(result, DelegatedAgentJsonContext.Default.DelegatedAgentTaskStartResult);
        }

        return AIFunctionFactory.Create(StartAutonomousSubAgentTaskAsync, new AIFunctionFactoryOptions
        {
            Name = "start_autonomous_subagent_task",
            Description = "创建临时后台子智能体任务。必填 childName、childInstructions、task、toolMountsJson；providerId、modelId、attachmentPathsJson、outputContract 可省略。返回 jobId，主智能体随后用普通工具调用查询进度和读取结果。"
        });
    }

    private AIFunction CreateGetSubAgentTaskStatusTool()
    {
        [Description("查询后台子智能体任务状态。")]
        string GetSubAgentTaskStatus([Description("后台子智能体任务 ID")] string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return SerializeError("jobId 不能为空。");
            }

            DelegatedAgentTaskStatusResult status;
            try
            {
                status = executor.GetStatus(jobId.Trim());
            }
            catch (InvalidOperationException ex)
            {
                return SerializeError(ex.Message);
            }
            return JsonSerializer.Serialize(status, DelegatedAgentJsonContext.Default.DelegatedAgentTaskStatusResult);
        }

        return AIFunctionFactory.Create(GetSubAgentTaskStatus, new AIFunctionFactoryOptions
        {
            Name = "get_subagent_task_status",
            Description = "查询后台子智能体任务状态。"
        });
    }

    private AIFunction CreateReadSubAgentTaskResultTool()
    {
        [Description("读取后台子智能体任务结果。任务未完成时返回当前进度摘要。")]
        string ReadSubAgentTaskResult([Description("后台子智能体任务 ID")] string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return SerializeError("jobId 不能为空。");
            }

            DelegatedAgentTaskResult result;
            try
            {
                result = executor.ReadResult(jobId.Trim());
            }
            catch (InvalidOperationException ex)
            {
                return SerializeError(ex.Message);
            }
            return JsonSerializer.Serialize(result, DelegatedAgentJsonContext.Default.DelegatedAgentTaskResult);
        }

        return AIFunctionFactory.Create(ReadSubAgentTaskResult, new AIFunctionFactoryOptions
        {
            Name = "read_subagent_task_result",
            Description = "读取后台子智能体任务结果。结果是普通工具调用结果，主智能体需要整理成普通消息回复用户。"
        });
    }

    private AIFunction CreateCancelSubAgentTaskTool()
    {
        [Description("取消后台子智能体任务。")]
        string CancelSubAgentTask([Description("后台子智能体任务 ID")] string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return SerializeError("jobId 不能为空。");
            }

            var result = executor.Cancel(jobId.Trim());
            return JsonSerializer.Serialize(result, DelegatedAgentJsonContext.Default.DelegatedAgentTaskCancelResult);
        }

        return AIFunctionFactory.Create(CancelSubAgentTask, new AIFunctionFactoryOptions
        {
            Name = "cancel_subagent_task",
            Description = "取消后台子智能体任务。"
        });
    }

    private static List<string> ParseStringArray(string? jsonOrText)
    {
        if (string.IsNullOrWhiteSpace(jsonOrText))
        {
            return [];
        }

        var text = jsonOrText.Trim();
        if (text.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                var array = JsonSerializer.Deserialize(text, DelegatedAgentJsonContext.Default.StringArray);
                return SystemToolCatalogService.NormalizeToolNames(array).ToList();
            }
            catch (JsonException)
            {
                // 回退到分隔符解析。
            }
        }

        return SystemToolCatalogService.NormalizeToolNames(
            text.Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
    }

    private static string SerializeError(string message)
    {
        return JsonSerializer.Serialize(
            new DelegatedAgentErrorResult(message),
            DelegatedAgentJsonContext.Default.DelegatedAgentErrorResult);
    }
}
