using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Drivers;
using Netor.Cortana.AI.Providers;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.TaskEngine.Agents;

/// <summary>
/// 轻量子智能体执行器。封装 IChatClient 调用 + JSON 响应解析。
/// 不创建 AIAgent 实例，直接通过 IChatClient.GetResponseAsync 发送消息。
/// 每次调用独立（无状态），适合 P4 编排器的无状态决策模型（doc 05 §1）。
///
/// 模型解析优先级：
///   TaskEngine.ModelId → Compaction.ModelId → AIAgentFactory.ChatClient → 工作流默认 Provider/Model。
/// </summary>
internal sealed partial class SubAgentRunner
{
    private const string TaskEngineModelKey = "TaskEngine.ModelId";
    private const string CompactionModelKey = "Compaction.ModelId";

    /// <summary>工作流默认 Provider / Model 配置键（与 WorkflowInputVm 保持一致）。</summary>
    private const string WorkflowDefaultProviderKey = "Workflow.DefaultProviderId";
    private const string WorkflowDefaultModelKey = "Workflow.DefaultModelId";

    private readonly ModelPurposeResolver _resolver;
    private readonly AIAgentFactory _agentFactory;
    private readonly AiProviderDriverRegistry _driverRegistry;
    private readonly AiProviderService _providerService;
    private readonly AiModelService _modelService;
    private readonly SystemSettingsService _systemSettings;
    private readonly GlobalLlmThrottle _throttle;
    private readonly ILogger<SubAgentRunner> _logger;

    /// <summary>缓存的回退客户端（从工作流默认配置构建）。</summary>
    private IChatClient? _cachedFallbackClient;
    private string? _cachedFallbackModelId;

    public SubAgentRunner(
        ModelPurposeResolver resolver,
        AIAgentFactory agentFactory,
        AiProviderDriverRegistry driverRegistry,
        AiProviderService providerService,
        AiModelService modelService,
        SystemSettingsService systemSettings,
        GlobalLlmThrottle throttle,
        ILogger<SubAgentRunner> logger)
    {
        _resolver = resolver;
        _agentFactory = agentFactory;
        _driverRegistry = driverRegistry;
        _providerService = providerService;
        _modelService = modelService;
        _systemSettings = systemSettings;
        _throttle = throttle;
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

        _logger.LogDebug("P4 SubAgent 调用开始（system={SystemLen}c, user={UserLen}c, throttle={Active}/{Max}）",
            systemPrompt.Length, userMessage.Length, _throttle.ActiveCount, _throttle.MaxConcurrency);

        using (await _throttle.AcquireAsync(ct).ConfigureAwait(false))
        {
            var response = await client.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            var text = response?.Text?.Trim() ?? string.Empty;

            _logger.LogDebug("P4 SubAgent 调用完成（response={ResponseLen}c）", text.Length);
            return text;
        }
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

            string text;
            using (await _throttle.AcquireAsync(ct).ConfigureAwait(false))
            {
                var response = await client.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
                text = response?.Text?.Trim() ?? string.Empty;
            }

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
    /// 交互式多轮对话。当 LLM 回复包含 [ASK_USER] 时，通过回调获取用户回答（异步等待）。
    /// 当 LLM 回复包含 [DONE] 或不含 [ASK_USER]/[CONTINUE] 时结束。
    /// 与 <see cref="RunMultiTurnAsync"/> 的区别：后者是 LLM 自我迭代，本方法支持真正的用户交互。
    /// </summary>
    /// <param name="systemPrompt">子智能体系统提示词。</param>
    /// <param name="userMessage">初始用户消息。</param>
    /// <param name="maxTurns">最大轮数（防死循环）。</param>
    /// <param name="onAskUser">
    ///   回调：LLM 提出问题 → 调用方负责获取用户回答。
    ///   参数：(轮次, 问题文本) → 返回用户回答文本。
    /// </param>
    /// <param name="onTurnCompleted">每轮完成通知。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>最终拼接的完整对话文本。</returns>
    public async Task<string> RunInteractiveAsync(
        string systemPrompt,
        string userMessage,
        int maxTurns,
        Func<int, string, Task<string>> onAskUser,
        Action<int, string>? onTurnCompleted,
        CancellationToken ct)
    {
        var client = ResolveClient();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userMessage),
        };

        _logger.LogDebug(
            "P4 SubAgent 交互式对话开始（maxTurns={MaxTurns}, system={SystemLen}c, user={UserLen}c）",
            maxTurns, systemPrompt.Length, userMessage.Length);

        var resultBuilder = new System.Text.StringBuilder();

        for (var turn = 1; turn <= maxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();

            string text;
            using (await _throttle.AcquireAsync(ct).ConfigureAwait(false))
            {
                var response = await client.GetResponseAsync(messages, cancellationToken: ct)
                    .ConfigureAwait(false);
                text = response?.Text?.Trim() ?? string.Empty;
            }

            _logger.LogDebug("P4 SubAgent 交互式对话 第{Turn}轮完成（response={ResponseLen}c）", turn, text.Length);

            resultBuilder.AppendLine(text);
            onTurnCompleted?.Invoke(turn, text);

            // [DONE] → 结束
            if (text.Contains("[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("P4 SubAgent 交互式对话结束于第{Turn}轮（[DONE]）", turn);
                break;
            }

            // [ASK_USER] → 提取问题，等待用户回答
            if (text.Contains("[ASK_USER]", StringComparison.OrdinalIgnoreCase))
            {
                var question = ExtractAfterMarker(text, "[ASK_USER]");
                _logger.LogDebug("P4 SubAgent 交互式对话 第{Turn}轮请求用户输入: {Question}",
                    turn, question.Length > 100 ? question[..100] + "…" : question);

                var userAnswer = await onAskUser(turn, question).ConfigureAwait(false);

                messages.Add(new ChatMessage(ChatRole.Assistant, text));
                messages.Add(new ChatMessage(ChatRole.User, userAnswer));
                continue;
            }

            // [CONTINUE] → LLM 自我迭代（不需要用户参与）
            if (text.Contains("[CONTINUE]", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add(new ChatMessage(ChatRole.Assistant, text));
                messages.Add(new ChatMessage(ChatRole.User, "请继续"));
                continue;
            }

            // 无标记 → 结束
            _logger.LogDebug("P4 SubAgent 交互式对话结束于第{Turn}轮（无标记）", turn);
            break;
        }

        return resultBuilder.ToString().Trim();
    }

    /// <summary>提取 [ASK_USER] 标记后面的问题文本。</summary>
    private static string ExtractAfterMarker(string text, string marker)
    {
        var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return text;
        var afterMarker = text[(idx + marker.Length)..].Trim();
        // 去除可能的尾部标记
        afterMarker = afterMarker.Replace("[DONE]", "", StringComparison.OrdinalIgnoreCase).Trim();
        afterMarker = afterMarker.Replace("[CONTINUE]", "", StringComparison.OrdinalIgnoreCase).Trim();
        // 如果标记后面没有内容，取标记前面的文本作为问题
        if (string.IsNullOrWhiteSpace(afterMarker))
        {
            afterMarker = text[..idx].Trim();
            // 去除其他标记
            afterMarker = afterMarker.Replace("[ASK_USER]", "", StringComparison.OrdinalIgnoreCase).Trim();
        }
        return string.IsNullOrWhiteSpace(afterMarker) ? "请提供更多信息" : afterMarker;
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
    // 工具调用支持
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 使用 <see cref="AIAgentFactory.BuildDynamicSubAgent"/> 构建带工具的子智能体并执行。
    /// 当步骤有 RequiredTools 时（如 file_write、shell 等），改用此方法替代纯文本的
    /// <see cref="RunMultiTurnAsync"/>，使 LLM 可以真正调用工具完成任务。
    /// </summary>
    /// <param name="systemPrompt">子智能体系统提示词（已替换所有占位符）。</param>
    /// <param name="userMessage">初始用户消息（任务指令 + 上下文）。</param>
    /// <param name="requiredTools">白名单工具名列表（plugin/MCP 工具名）。</param>
    /// <param name="agentName">智能体显示名（用于日志）。</param>
    /// <param name="onText">每次 LLM 输出文本时的回调（用于推送到 UI 对话流）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>智能体最终输出的文本（工具调用结果已合并）。</returns>
    public async Task<string> RunWithToolsAsync(
        string systemPrompt,
        string userMessage,
        IReadOnlyList<string> requiredTools,
        string agentName,
        Action<string>? onText,
        CancellationToken ct)
    {
        // 解析 provider/model 实体（复用与 ResolveClient 相同的优先级逻辑）
        var (provider, model) = ResolveProviderAndModel();
        if (provider is null || model is null)
        {
            _logger.LogWarning("P4 RunWithToolsAsync: 无法解析 Provider/Model，回退到纯文本执行");
            return await RunMultiTurnAsync(systemPrompt, userMessage, 8,
                (_, text) => onText?.Invoke(CleanLlmMarkers(text)), ct).ConfigureAwait(false);
        }

        _logger.LogDebug(
            "P4 RunWithToolsAsync: 构建工具子智能体 [{Name}]，工具={Tools}，Provider={Provider}，Model={Model}",
            agentName, string.Join(",", requiredTools), provider.Name, model.Name);

        var agent = _agentFactory.BuildDynamicSubAgent(agentName, systemPrompt, provider, model, requiredTools);

        var messages = new List<ChatMessage> { new(ChatRole.User, userMessage) };

        using (await _throttle.AcquireAsync(ct).ConfigureAwait(false))
        {
            var response = await agent.RunAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            var text = response?.Text?.Trim() ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(text))
                onText?.Invoke(CleanLlmMarkers(text));

            _logger.LogDebug("P4 RunWithToolsAsync [{Name}] 完成（{Len}c）", agentName, text.Length);
            return text;
        }
    }

    /// <summary>
    /// 解析 Provider / Model 实体（与 ResolveClient 相同的优先级，但返回实体而非 IChatClient）。
    /// 优先级：TaskEngine.ModelId → Compaction.ModelId → 工作流默认 Provider/Model → 全局第一个。
    /// </summary>
    private (Netor.Cortana.Entitys.AiProviderEntity? provider, Netor.Cortana.Entitys.AiModelEntity? model)
        ResolveProviderAndModel()
    {
        try
        {
            // 按优先级尝试获取 modelId
            var modelId = _systemSettings.GetValue<string>(TaskEngineModelKey, string.Empty);
            if (string.IsNullOrEmpty(modelId))
                modelId = _systemSettings.GetValue<string>(CompactionModelKey, string.Empty);
            if (string.IsNullOrEmpty(modelId))
                modelId = _systemSettings.GetValue<string>(WorkflowDefaultModelKey, string.Empty);

            // 全局第一个可用模型（最后回退）
            if (string.IsNullOrEmpty(modelId))
            {
                var defaultProvider = _providerService.GetAll().FirstOrDefault(p => p.IsDefault)
                                      ?? _providerService.GetAll().FirstOrDefault();
                if (defaultProvider is null) return (null, null);

                var defaultModel = _modelService.GetByProviderId(defaultProvider.Id)
                                       .FirstOrDefault(m => m.IsDefault)
                                   ?? _modelService.GetByProviderId(defaultProvider.Id).FirstOrDefault();
                if (defaultModel is null) return (null, null);

                return (defaultProvider, defaultModel);
            }

            var resolvedModel = _modelService.GetById(modelId);
            if (resolvedModel is null) return (null, null);

            var providerId = _systemSettings.GetValue<string>(WorkflowDefaultProviderKey, string.Empty);
            var resolvedProvider = !string.IsNullOrEmpty(providerId)
                ? _providerService.GetById(providerId)
                : _providerService.GetById(resolvedModel.ProviderId);

            return (resolvedProvider, resolvedModel);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "P4 ResolveProviderAndModel 失败");
            return (null, null);
        }
    }

    private static string CleanLlmMarkers(string text)
    {
        return text
            .Replace("[DONE]", "", StringComparison.OrdinalIgnoreCase)
            .Replace("[CONTINUE]", "", StringComparison.OrdinalIgnoreCase)
            .Replace("[ASK_USER]", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }
    /// 解析可用的 IChatClient。
    /// 优先级：TaskEngine.ModelId → Compaction.ModelId → AIAgentFactory.ChatClient → 工作流默认 Provider/Model。
    /// </summary>
    private IChatClient ResolveClient()
    {
        // 1. 专用任务引擎模型
        var client = _resolver.TryResolve(TaskEngineModelKey);
        if (client is not null) return client;

        // 2. 压缩/摘要模型（通常是轻量快速模型）
        client = _resolver.TryResolve(CompactionModelKey);
        if (client is not null) return client;

        // 3. 当前主对话模型（仅当用户已发起过对话时可用）
        if (_agentFactory.ChatClient is not null)
            return _agentFactory.ChatClient;

        // 4. 从工作流默认 Provider/Model 配置直接构建 IChatClient
        //    （用户在 UI 上选择的默认厂商/模型，即使未发起过对话也可用）
        client = ResolveFromWorkflowDefaults();
        if (client is not null) return client;

        throw new InvalidOperationException(
            "P4 任务引擎无可用的 LLM 客户端。请在设置中配置 TaskEngine.ModelId 或 Compaction.ModelId，" +
            "或确保当前有活跃的对话智能体，或在工作流中选择默认厂商和模型。");
    }

    /// <summary>
    /// 从工作流默认 Provider/Model 配置构建 IChatClient（第 4 回退路径）。
    /// 读取 SystemSettings 中 Workflow.DefaultProviderId / Workflow.DefaultModelId，
    /// 通过 AiProviderDriverRegistry 直接构建。结果按 ModelId 缓存，配置变更时重建。
    /// </summary>
    private IChatClient? ResolveFromWorkflowDefaults()
    {
        try
        {
            var modelId = _systemSettings.GetValue<string>(WorkflowDefaultModelKey, string.Empty);
            if (string.IsNullOrEmpty(modelId))
            {
                // 无显式配置 → 尝试取第一个可用模型
                var defaultProvider = _providerService.GetAll().FirstOrDefault(p => p.IsDefault)
                                     ?? _providerService.GetAll().FirstOrDefault();
                if (defaultProvider is null) return null;

                var defaultModel = _modelService.GetByProviderId(defaultProvider.Id).FirstOrDefault(m => m.IsDefault)
                                   ?? _modelService.GetByProviderId(defaultProvider.Id).FirstOrDefault();
                if (defaultModel is null) return null;

                modelId = defaultModel.Id;
            }

            // 缓存命中
            if (_cachedFallbackClient is not null &&
                string.Equals(_cachedFallbackModelId, modelId, StringComparison.Ordinal))
            {
                return _cachedFallbackClient;
            }

            var model = _modelService.GetById(modelId);
            if (model is null) return null;

            var providerId = _systemSettings.GetValue<string>(WorkflowDefaultProviderKey, string.Empty);
            var provider = !string.IsNullOrEmpty(providerId)
                ? _providerService.GetById(providerId)
                : _providerService.GetById(model.ProviderId);
            if (provider is null) return null;

            var driver = _driverRegistry.Resolve(provider);
            var newClient = driver.CreateChatClient(provider, model);

            // 替换缓存（释放旧实例）
            _cachedFallbackClient?.Dispose();
            _cachedFallbackClient = newClient;
            _cachedFallbackModelId = modelId;

            _logger.LogInformation(
                "P4 SubAgentRunner 从工作流默认配置构建 IChatClient: Provider={Provider}, Model={Model}",
                provider.Name, model.Name);

            return newClient;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "P4 SubAgentRunner 从工作流默认配置构建 IChatClient 失败");
            return null;
        }
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
