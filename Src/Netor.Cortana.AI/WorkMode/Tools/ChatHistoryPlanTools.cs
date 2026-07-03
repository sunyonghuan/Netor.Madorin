using System.ComponentModel;
using System.Text;

using Microsoft.Extensions.AI;

using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.WorkMode.Tools;

/// <summary>
/// 从聊天历史中提取计划参考材料的工具。
/// </summary>
public sealed class ChatHistoryPlanTools
{
    private const int MaxMessages = 24;
    private const int MaxContentLength = 1200;

    private readonly WorkTaskService _taskService;
    private readonly ChatMessageService _chatMessageService;
    private readonly string _taskId;

    public ChatHistoryPlanTools(
        WorkTaskService taskService,
        ChatMessageService chatMessageService,
        string taskId)
    {
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _chatMessageService = chatMessageService ?? throw new ArgumentNullException(nameof(chatMessageService));
        _taskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
    }

    public AIFunction CreateLoadPlanFromChatHistoryTool()
    {
        [Description("读取当前会话或指定会话的最近对话内容，作为制定工作计划的参考。")]
        Task<string> LoadPlanFromChatHistoryAsync(
            [Description("可选：来源会话 ID。为空时使用当前工作任务所属会话。")] string? sourceSessionId,
            CancellationToken ct)
        {
            return Task.FromResult(BuildHistoryReference(sourceSessionId));
        }

        return AIFunctionFactory.Create(LoadPlanFromChatHistoryAsync, new AIFunctionFactoryOptions
        {
            Name = "load_plan_from_chat_history",
            Description = "读取聊天历史作为制定工作计划的参考材料。工具只提供材料，最终计划仍需调用 set_plan 写入。"
        });
    }

    public AIFunction CreateLoadPlanFromGroupChatTool()
    {
        [Description("读取群聊/多智能体会话历史，作为制定工作计划的参考。当前实现复用聊天历史读取。")]
        Task<string> LoadPlanFromGroupChatAsync(
            [Description("来源群聊会话 ID。为空时使用当前工作任务所属会话。")] string? sourceSessionId,
            CancellationToken ct)
        {
            return Task.FromResult(BuildHistoryReference(sourceSessionId));
        }

        return AIFunctionFactory.Create(LoadPlanFromGroupChatAsync, new AIFunctionFactoryOptions
        {
            Name = "load_plan_from_groupchat",
            Description = "读取群聊/多智能体会话历史作为计划参考材料。当前与 load_plan_from_chat_history 使用同一读取链路。"
        });
    }

    private string BuildHistoryReference(string? sourceSessionId)
    {
        var task = _taskService.GetById(_taskId);
        if (task is null)
        {
            return "错误：当前任务不存在";
        }

        var sessionId = string.IsNullOrWhiteSpace(sourceSessionId)
            ? task.SessionId
            : sourceSessionId.Trim();

        var messages = _chatMessageService.GetBySessionId(sessionId)
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .TakeLast(MaxMessages)
            .ToList();

        if (messages.Count == 0)
        {
            return "没有找到可用于制定计划的对话历史。";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"来源会话：{sessionId}");
        sb.AppendLine("最近对话摘要材料：");
        sb.AppendLine();

        foreach (var message in messages)
        {
            var role = NormalizeRole(message.Role);
            var author = string.IsNullOrWhiteSpace(message.AgentName)
                ? message.AuthorName
                : message.AgentName;
            var content = Truncate(message.Content.Trim(), MaxContentLength);

            sb.AppendLine($"[{role}] {author}");
            sb.AppendLine(content);
            sb.AppendLine();
        }

        sb.AppendLine("请基于以上材料调用 set_plan 制定结构化工作计划；不要把上述材料原样输出给用户。");
        return sb.ToString();
    }

    private static string NormalizeRole(string role)
    {
        return role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "用户"
            : role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "助手"
            : role;
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
