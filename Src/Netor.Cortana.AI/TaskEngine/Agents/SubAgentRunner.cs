using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Providers;

namespace Netor.Cortana.AI.TaskEngine.Agents;

/// <summary>
/// 轻量子智能体执行器。封装 IChatClient 调用 + JSON 响应解析。
/// 不创建 AIAgent 实例，直接通过 IChatClient.GetResponseAsync 发送消息。
/// 每次调用独立（无状态），适合 P4 编排器的无状态决策模型（doc 05 §1）。
///
/// 模型解析优先级：TaskEngine.ModelId → Compaction.ModelId → AIAgentFactory.ChatClient。
/// </summary>
internal sealed partial class SubAgentRunner
{
    private const string TaskEngineModelKey = "TaskEngine.ModelId";
    private const string CompactionModelKey = "Compaction.ModelId";

    private readonly ModelPurposeResolver _resolver;
    private readonly AIAgentFactory _agentFactory;
    private readonly ILogger<SubAgentRunner> _logger;

    public SubAgentRunner(
        ModelPurposeResolver resolver,
        AIAgentFactory agentFactory,
        ILogger<SubAgentRunner> logger)
    {
        _resolver = resolver;
        _agentFactory = agentFactory;
        _logger = logger;
    }

    // ══════════════════════════════════════════════════════════════════════
    // 公开 API
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 发送 system + user 消息并获取文本响应。
    /// </summary>
    /// <param name="systemPrompt">子智能体的系统提示词。</param>
    /// <param name="userMessage">用户消息（任务描述/上下文）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>LLM 的文本响应。</returns>
    /// <exception cref="InvalidOperationException">无可用的 IChatClient。</exception>
    public async Task<string> RunAsync(string systemPrompt, string userMessage, CancellationToken ct)
    {
        var client = ResolveClient();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userMessage),
        };

        _logger.LogDebug("P4 SubAgent 调用开始（system={SystemLen}c, user={UserLen}c）",
            systemPrompt.Length, userMessage.Length);

        var response = await client.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
        var text = response?.Text?.Trim() ?? string.Empty;

        _logger.LogDebug("P4 SubAgent 调用完成（response={ResponseLen}c）", text.Length);
        return text;
    }

    /// <summary>
    /// 发送消息并将响应解析为 JSON 对象。
    /// 支持从 markdown ```json 代码块中提取 JSON，也支持直接 JSON 响应。
    /// </summary>
    /// <typeparam name="T">目标反序列化类型。</typeparam>
    /// <param name="systemPrompt">子智能体的系统提示词。</param>
    /// <param name="userMessage">用户消息。</param>
    /// <param name="typeInfo">AOT 兼容的 JsonTypeInfo。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>反序列化后的对象；解析失败时返回 null。</returns>
    public async Task<T?> RunJsonAsync<T>(
        string systemPrompt,
        string userMessage,
        JsonTypeInfo<T> typeInfo,
        CancellationToken ct) where T : class
    {
        var rawText = await RunAsync(systemPrompt, userMessage, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(rawText))
        {
            _logger.LogWarning("P4 SubAgent 返回空响应，无法解析 JSON");
            return null;
        }

        // 尝试从 ```json ... ``` 代码块中提取
        var jsonText = ExtractJsonBlock(rawText);

        try
        {
            return JsonSerializer.Deserialize(jsonText, typeInfo);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "P4 SubAgent JSON 反序列化失败，原始响应前 500 字符: {Preview}",
                rawText.Length > 500 ? rawText[..500] : rawText);
            return null;
        }
    }

    /// <summary>
    /// 多轮对话执行。支持 LLM 自我迭代（如果 LLM 回复中包含 [CONTINUE] 标记则继续对话）。
    /// 每轮结果通过 onTurnCompleted 回调通知调用方（用于 UI 进度更新）。
    /// </summary>
    /// <param name="systemPrompt">子智能体的系统提示词。</param>
    /// <param name="userMessage">初始用户消息。</param>
    /// <param name="maxTurns">最大对话轮数，防止无限循环。</param>
    /// <param name="onTurnCompleted">每轮完成后的回调，参数为 (轮次序号, 本轮响应文本)。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>最终拼接的完整响应文本。</returns>
    public async Task<string> RunMultiTurnAsync(
        string systemPrompt,
        string userMessage,
        int maxTurns,
        Action<int, string>? onTurnCompleted,
        CancellationToken ct)
    {
        var client = ResolveClient();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userMessage),
        };

        _logger.LogDebug("P4 SubAgent 多轮对话开始（maxTurns={MaxTurns}, system={SystemLen}c, user={UserLen}c）",
            maxTurns, systemPrompt.Length, userMessage.Length);

        var resultBuilder = new System.Text.StringBuilder();

        for (var turn = 1; turn <= maxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();

            var response = await client.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            var text = response?.Text?.Trim() ?? string.Empty;

            _logger.LogDebug("P4 SubAgent 多轮对话 第{Turn}轮完成（response={ResponseLen}c）", turn, text.Length);

            resultBuilder.AppendLine(text);
            onTurnCompleted?.Invoke(turn, text);

            // 判断是否需要继续
            if (text.Contains("[DONE]", StringComparison.OrdinalIgnoreCase) ||
                !text.Contains("[CONTINUE]", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("P4 SubAgent 多轮对话结束于第{Turn}轮", turn);
                break;
            }

            // 追加本轮对话到历史，发送继续指令
            messages.Add(new ChatMessage(ChatRole.Assistant, text));
            messages.Add(new ChatMessage(ChatRole.User, "请继续"));
        }

        return resultBuilder.ToString().Trim();
    }

    /// <summary>
    /// 多轮对话执行并将最终响应解析为 JSON 对象。
    /// </summary>
    /// <typeparam name="T">目标反序列化类型。</typeparam>
    /// <param name="systemPrompt">子智能体的系统提示词。</param>
    /// <param name="userMessage">初始用户消息。</param>
    /// <param name="maxTurns">最大对话轮数。</param>
    /// <param name="typeInfo">AOT 兼容的 JsonTypeInfo。</param>
    /// <param name="onTurnCompleted">每轮完成后的回调。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>反序列化后的对象；解析失败时返回 null。</returns>
    public async Task<T?> RunMultiTurnJsonAsync<T>(
        string systemPrompt,
        string userMessage,
        int maxTurns,
        JsonTypeInfo<T> typeInfo,
        Action<int, string>? onTurnCompleted,
        CancellationToken ct) where T : class
    {
        var rawText = await RunMultiTurnAsync(systemPrompt, userMessage, maxTurns, onTurnCompleted, ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(rawText))
        {
            _logger.LogWarning("P4 SubAgent 多轮对话返回空响应，无法解析 JSON");
            return null;
        }

        var jsonText = ExtractJsonBlock(rawText);

        try
        {
            return JsonSerializer.Deserialize(jsonText, typeInfo);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "P4 SubAgent 多轮对话 JSON 反序列化失败，原始响应前 500 字符: {Preview}",
                rawText.Length > 500 ? rawText[..500] : rawText);
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // 内部辅助
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 解析可用的 IChatClient。
    /// 优先级：TaskEngine.ModelId → Compaction.ModelId → AIAgentFactory.ChatClient。
    /// </summary>
    private IChatClient ResolveClient()
    {
        // 1. 专用任务引擎模型
        var client = _resolver.TryResolve(TaskEngineModelKey);
        if (client is not null) return client;

        // 2. 压缩/摘要模型（通常是轻量快速模型）
        client = _resolver.TryResolve(CompactionModelKey);
        if (client is not null) return client;

        // 3. 当前主对话模型
        if (_agentFactory.ChatClient is not null)
            return _agentFactory.ChatClient;

        throw new InvalidOperationException(
            "P4 任务引擎无可用的 LLM 客户端。请在设置中配置 TaskEngine.ModelId 或 Compaction.ModelId，" +
            "或确保当前有活跃的对话智能体。");
    }

    /// <summary>
    /// 从 LLM 响应中提取 JSON 文本。
    /// 优先匹配 ```json ... ``` 代码块，其次尝试匹配 { ... } 或 [ ... ] 块，最后返回原文。
    /// </summary>
    internal static string ExtractJsonBlock(string text)
    {
        // 1. 匹配 ```json ... ```
        var match = JsonCodeBlockRegex().Match(text);
        if (match.Success)
            return match.Groups[1].Value.Trim();

        // 2. 匹配 ``` ... ```（无语言标记）
        match = CodeBlockRegex().Match(text);
        if (match.Success)
        {
            var inner = match.Groups[1].Value.Trim();
            if (inner.StartsWith('{') || inner.StartsWith('['))
                return inner;
        }

        // 3. 尝试找到第一个 { 到最后一个 } 的范围
        var firstBrace = text.IndexOf('{');
        var lastBrace = text.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
            return text[firstBrace..(lastBrace + 1)];

        // 4. 尝试数组
        var firstBracket = text.IndexOf('[');
        var lastBracket = text.LastIndexOf(']');
        if (firstBracket >= 0 && lastBracket > firstBracket)
            return text[firstBracket..(lastBracket + 1)];

        // 5. 返回原文（让调用方的 JsonSerializer 尝试解析）
        return text;
    }

    [GeneratedRegex(@"```json\s*\n([\s\S]*?)\n\s*```", RegexOptions.Compiled)]
    private static partial Regex JsonCodeBlockRegex();

    [GeneratedRegex(@"```\s*\n([\s\S]*?)\n\s*```", RegexOptions.Compiled)]
    private static partial Regex CodeBlockRegex();
}
