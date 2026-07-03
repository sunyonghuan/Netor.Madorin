using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Providers;
using Netor.Cortana.AI.WorkMode.Prompts;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Netor.Cortana.AI.MeetingMode;

/// <summary>
/// 会议上下文压缩服务：把早期完整消息生成不可变摘要段，只服务 LLM history。
/// </summary>
public sealed class MeetingCompactionService
{
    private const int DefaultSegmentSize = 30;
    private const int DefaultRawTailSize = 20;

    private readonly MeetingMessageService _messages;
    private readonly MeetingCompactionSegmentService _segments;
    private readonly SystemSettingsService _settings;
    private readonly IChatCompactionClientResolver _clientResolver;
    private readonly IPromptProvider _promptProvider;
    private readonly ILogger<MeetingCompactionService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public MeetingCompactionService(
        MeetingMessageService messages,
        MeetingCompactionSegmentService segments,
        SystemSettingsService settings,
        IChatCompactionClientResolver clientResolver,
        IPromptProvider promptProvider,
        ILogger<MeetingCompactionService> logger)
    {
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        _segments = segments ?? throw new ArgumentNullException(nameof(segments));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clientResolver = clientResolver ?? throw new ArgumentNullException(nameof(clientResolver));
        _promptProvider = promptProvider ?? throw new ArgumentNullException(nameof(promptProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>满足阈值时为指定会议新增一个压缩段；失败只记录日志，不影响会议主流程。</summary>
    public async Task TryCompactAsync(
        string meetingId,
        AIAgent fallbackAgent,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        ArgumentNullException.ThrowIfNull(fallbackAgent);

        var gate = _locks.GetOrAdd(meetingId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await CompactCoreAsync(meetingId, fallbackAgent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("会议 {MeetingId} 压缩已取消", meetingId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "会议 {MeetingId} 压缩失败，已跳过本次触发", meetingId);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task CompactCoreAsync(
        string meetingId,
        AIAgent fallbackAgent,
        CancellationToken cancellationToken)
    {
        var segmentSize = Math.Max(1, _settings.GetValue("Meeting.Compaction.SegmentSize", DefaultSegmentSize));
        var rawTailSize = Math.Max(0, _settings.GetValue("Meeting.Compaction.RawTailSize", DefaultRawTailSize));
        var coveredEndSequence = _segments.GetMaxEndSequence(meetingId);
        var candidates = _messages.ListCompleteBySequence(meetingId, coveredEndSequence + 1);

        if (candidates.Count <= segmentSize + rawTailSize)
        {
            return;
        }

        var batch = candidates.Take(segmentSize).ToList();
        if (batch.Count < segmentSize)
        {
            return;
        }

        var client = _clientResolver.Resolve(fallbackAgent);
        if (client is null)
        {
            _logger.LogDebug("会议 {MeetingId} 未解析到压缩模型，跳过压缩", meetingId);
            return;
        }

        var summary = await GenerateSummaryAsync(client, batch, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return;
        }

        var segmentIndex = _segments.GetMaxSegmentIndex(meetingId) + 1;
        _segments.Add(new MeetingCompactionSegmentEntity
        {
            MeetingId = meetingId,
            SegmentIndex = segmentIndex,
            StartSequence = batch[0].Sequence,
            EndSequence = batch[^1].Sequence,
            Summary = summary.Trim(),
            OriginalMessageCount = batch.Count,
            ModelName = ResolveModelName(fallbackAgent),
        });

        _logger.LogInformation(
            "会议 {MeetingId} 新增压缩段 #{SegmentIndex}：消息序号 [{Start}..{End}]，摘要 {Length} 字符",
            meetingId,
            segmentIndex,
            batch[0].Sequence,
            batch[^1].Sequence,
            summary.Length);
    }

    private async Task<string?> GenerateSummaryAsync(
        IChatClient client,
        IReadOnlyList<MeetingMessageEntity> batch,
        CancellationToken cancellationToken)
    {
        var systemPrompt = await _promptProvider
                .GetPromptAsync("meeting.compaction", cancellationToken)
                .ConfigureAwait(false)
            ?? "你是会议摘要助手。请压缩会议片段，保留发言人、决策、待办、数字、日期、路径、URL 和工具结果。";

        var prompt = new List<AIChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, BuildTranscript(batch)),
            new(ChatRole.User, "请基于以上会议片段生成结构化摘要。")
        };

        using var _ = (client as TokenTrackingChatClient)?.SuppressUsage();
        var completion = await client.GetResponseAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        return completion?.Text;
    }

    private static string BuildTranscript(IEnumerable<MeetingMessageEntity> messages)
    {
        var sb = new StringBuilder();
        foreach (var message in messages)
        {
            sb.AppendLine($"## #{message.Sequence} {ResolveSpeakerLabel(message)}");
            sb.AppendLine(message.ContentMd);

            if (!string.IsNullOrWhiteSpace(message.ThinkingMd))
            {
                sb.AppendLine();
                sb.AppendLine("[思考摘要]");
                sb.AppendLine(message.ThinkingMd);
            }

            if (!string.IsNullOrWhiteSpace(message.ToolCallsJson))
            {
                sb.AppendLine();
                sb.AppendLine("[工具调用]");
                sb.AppendLine(CompactJson(message.ToolCallsJson));
            }

            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    private static string ResolveSpeakerLabel(MeetingMessageEntity message)
    {
        if (string.Equals(message.SpeakerKind, "host", StringComparison.OrdinalIgnoreCase))
        {
            return "主持人";
        }

        if (string.Equals(message.SpeakerKind, "user", StringComparison.OrdinalIgnoreCase))
        {
            return "用户";
        }

        return string.IsNullOrWhiteSpace(message.SpeakerName)
            ? message.SpeakerId
            : message.SpeakerName;
    }

    private static string CompactJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string ResolveModelName(AIAgent agent)
    {
        try
        {
            return agent.GetService<IChatClient>()?.GetType().Name ?? "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }
}
