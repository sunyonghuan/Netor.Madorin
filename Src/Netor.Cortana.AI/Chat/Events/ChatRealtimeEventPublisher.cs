using Microsoft.Extensions.AI;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI;

/// <summary>
/// 发布专家模式的实时过程事件。
/// 阶段 3.2 将推理 / 工具过程的桥接逻辑从 <see cref="AiChatHostedService"/> 中抽离。
/// </summary>
public sealed class ChatRealtimeEventPublisher(IRealtimeProcessOutput? realtimeOutput)
{
    public async Task PublishProcessEventsAsync(
        string turnId,
        string reasoningProcessId,
        IEnumerable<AIContent> contents,
        CancellationToken cancellationToken)
    {
        if (realtimeOutput is null)
        {
            return;
        }

        foreach (var content in contents)
        {
            switch (content)
            {
                case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                    await PublishProcessEventAsync(
                        turnId,
                        reasoningProcessId,
                        "thinking",
                        "思考过程",
                        "running",
                        reasoning.Text,
                        null,
                        0,
                        cancellationToken);
                    break;

                case FunctionCallContent functionCall:
                    await PublishToolProcessEventAsync(
                        turnId,
                        functionCall.CallId,
                        functionCall.Name,
                        "running",
                        SerializeContent(functionCall.Arguments),
                        null,
                        cancellationToken);
                    break;

                case McpServerToolCallContent mcpToolCall:
                    await PublishToolProcessEventAsync(
                        turnId,
                        mcpToolCall.CallId,
                        $"{mcpToolCall.ServerName}.{mcpToolCall.Name}",
                        "running",
                        SerializeContent(mcpToolCall.Arguments),
                        null,
                        cancellationToken);
                    break;

                case ToolCallContent toolCall:
                    await PublishToolProcessEventAsync(
                        turnId,
                        toolCall.CallId,
                        "工具调用",
                        "running",
                        string.Empty,
                        null,
                        cancellationToken);
                    break;

                case FunctionResultContent functionResult:
                    await PublishToolProcessEventAsync(
                        turnId,
                        functionResult.CallId,
                        "工具调用",
                        functionResult.Exception is null ? "success" : "failed",
                        functionResult.Exception?.ToString() ?? SerializeContent(functionResult.Result),
                        null,
                        cancellationToken);
                    break;

                case McpServerToolResultContent mcpToolResult:
                    await PublishToolProcessEventAsync(
                        turnId,
                        mcpToolResult.CallId,
                        "MCP 工具",
                        "success",
                        SerializeContent(mcpToolResult.Outputs),
                        null,
                        cancellationToken);
                    break;

                case ToolResultContent toolResult:
                    await PublishToolProcessEventAsync(
                        turnId,
                        toolResult.CallId,
                        "工具调用",
                        "success",
                        string.Empty,
                        null,
                        cancellationToken);
                    break;
            }
        }
    }

    public Task PublishToolProcessEventAsync(
        string turnId,
        string? callId,
        string title,
        string status,
        string content,
        int? exitCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(callId))
        {
            return Task.CompletedTask;
        }

        return PublishProcessEventAsync(
            turnId,
            callId,
            "tool",
            string.IsNullOrWhiteSpace(title) ? "工具调用" : title,
            status,
            content,
            exitCode,
            0,
            cancellationToken);
    }

    public Task PublishProcessEventAsync(
        string turnId,
        string processId,
        string kind,
        string title,
        string status,
        string content,
        int? exitCode,
        long durationMs,
        CancellationToken cancellationToken)
    {
        if (realtimeOutput is null)
        {
            return Task.CompletedTask;
        }

        return realtimeOutput.OnProcessEventAsync(new RealtimeProcessEvent
        {
            TurnId = turnId,
            ProcessId = processId,
            Kind = kind,
            Title = title,
            Status = status,
            Content = content,
            ExitCode = exitCode,
            DurationMs = durationMs,
            Timestamp = DateTimeOffset.UtcNow,
        }, cancellationToken);
    }

    private static string SerializeContent(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        return value is string text ? text : value.ToString() ?? string.Empty;
    }
}
