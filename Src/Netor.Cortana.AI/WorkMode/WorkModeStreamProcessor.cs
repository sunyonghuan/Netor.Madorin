using System.Text;
using System.Text.Json;

using Microsoft.Extensions.AI;

using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.AI.WorkMode.Models;
using Netor.Cortana.AI.WorkMode.Reliability;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AI.WorkMode;

/// <summary>
/// 工作模式流式处理器。
/// 消费 RunStreamingAsync 的 chunk，解析 thinking/tool call/text，
/// 发布对应的 work.* 事件驱动 UI 卡片渲染。
/// 职责单一：只做"流 → 事件"转换，不持有 UI 引用。
/// </summary>
public sealed class WorkModeStreamProcessor
{
    private readonly string _taskId;
    private readonly IPublisher _publisher;
    private readonly WorkExecutionLogService _logService;
    private readonly DeadlockDetector? _deadlockDetector;
    private readonly string? _authorName;
    private readonly string? _callIdPrefix;
    private readonly bool _persistThinking;

    private readonly StringBuilder _textBuffer = new();

    public WorkModeStreamProcessor(
        string taskId,
        IPublisher publisher,
        WorkExecutionLogService logService,
        DeadlockDetector? deadlockDetector = null,
        string? authorName = null,
        string? callIdPrefix = null,
        bool persistThinking = false)
    {
        _taskId = taskId;
        _publisher = publisher;
        _logService = logService;
        _deadlockDetector = deadlockDetector;
        _authorName = string.IsNullOrWhiteSpace(authorName) ? null : authorName.Trim();
        _callIdPrefix = string.IsNullOrWhiteSpace(callIdPrefix) ? null : callIdPrefix.Trim();
        _persistThinking = persistThinking;
    }

    /// <summary>
    /// 处理一个流式 chunk 中的所有 content 项。
    /// </summary>
    public async Task ProcessChunkAsync(IEnumerable<AIContent> contents, CancellationToken ct)
    {
        foreach (var content in contents)
        {
            switch (content)
            {
                case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                    await OnThinkingAsync(reasoning.Text, ct);
                    break;

                case FunctionCallContent functionCall:
                    await OnToolCallAsync(functionCall.CallId, functionCall.Name, functionCall.Arguments, ct);
                    break;

                case FunctionResultContent functionResult:
                    await OnToolResultAsync(functionResult.CallId, functionResult.Result, functionResult.Exception, ct);
                    break;

                case TextContent text when !string.IsNullOrEmpty(text.Text):
                    _textBuffer.Append(text.Text);
                    await _publisher.PublishAsync(
                        Events.OnWorkAssistantDelta,
                        new WorkAssistantDeltaArgs(_taskId, _authorName, text.Text));
                    break;
            }
        }
    }

    /// <summary>
    /// 流结束后刷出剩余文本缓冲。
    /// </summary>
    public Task FlushAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// 获取本轮累积的完整文本（用于追加到对话历史）。
    /// </summary>
    public string GetAccumulatedText() => _textBuffer.ToString();

    private Task OnThinkingAsync(string text, CancellationToken ct)
    {
        if (_persistThinking)
        {
            var logRecord = new WorkExecutionLogRecords.Thinking(_authorName, text);
            _logService.Append(_taskId, WorkExecutionLogTypes.Thinking,
                JsonSerializer.Serialize(logRecord, WorkModeJsonContext.Default.Thinking));
        }

        return _publisher.PublishAsync(
            Events.OnWorkAssistantDelta,
            new WorkAssistantDeltaArgs(_taskId, BuildAuthorName("thinking"), text));
    }

    private Task OnToolCallAsync(string callId, string toolName, object? arguments, CancellationToken ct)
    {
        var paramsJson = arguments is not null
            ? FormatToolArguments(arguments)
            : null;

        var displayCallId = BuildCallId(callId);
        var displayToolName = BuildToolName(toolName);
        var logRecord = new WorkExecutionLogRecords.ToolCall(displayCallId, displayToolName, paramsJson);
        _logService.Append(_taskId, WorkExecutionLogTypes.ToolCall,
            JsonSerializer.Serialize(logRecord, WorkModeJsonContext.Default.ToolCall));

        var detection = _deadlockDetector?.CheckToolCall(_taskId, displayToolName, paramsJson);
        if (detection is { IsDeadlock: true, Reason: not null })
        {
            _deadlockDetector!.Report(_taskId, detection.Reason);
        }

        return _publisher.PublishAsync(
            Events.OnWorkToolCall,
            new WorkToolCallArgs(_taskId, displayCallId, displayToolName, paramsJson));
    }

    private Task OnToolResultAsync(string callId, object? result, Exception? exception, CancellationToken ct)
    {
        var status = exception is null ? "success" : "failed";
        var resultText = result?.ToString();
        var error = exception?.Message;

        var displayCallId = BuildCallId(callId);
        var logRecord = new WorkExecutionLogRecords.ToolResult(displayCallId, status, resultText, error);
        _logService.Append(_taskId, WorkExecutionLogTypes.ToolResult,
            JsonSerializer.Serialize(logRecord, WorkModeJsonContext.Default.ToolResult));

        return _publisher.PublishAsync(
            Events.OnWorkToolResult,
            new WorkToolResultArgs(_taskId, displayCallId, status, resultText, error));
    }

    private string? BuildAuthorName(string fallback)
        => _authorName is null ? fallback : $"{_authorName}:{fallback}";

    private string BuildCallId(string callId)
        => _callIdPrefix is null ? callId : $"{_callIdPrefix}:{callId}";

    private string BuildToolName(string toolName)
        => _authorName is null ? toolName : $"{_authorName}.{toolName}";

    private static string FormatToolArguments(object arguments)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        WriteJsonValue(writer, arguments);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case IReadOnlyDictionary<string, object?> dictionary:
                writer.WriteStartObject();
                foreach (var (key, item) in dictionary)
                {
                    writer.WritePropertyName(key);
                    WriteJsonValue(writer, item);
                }
                writer.WriteEndObject();
                break;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                writer.WriteStartObject();
                foreach (var (key, item) in pairs)
                {
                    writer.WritePropertyName(key);
                    WriteJsonValue(writer, item);
                }
                writer.WriteEndObject();
                break;
            case IEnumerable<object?> items:
                writer.WriteStartArray();
                foreach (var item in items)
                {
                    WriteJsonValue(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}
