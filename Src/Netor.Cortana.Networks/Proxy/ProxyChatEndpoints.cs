// =============================================================================
// ProxyChatEndpoints.cs - Ollama 原生聊天端点处理器
// =============================================================================
// 功能说明：
// 实现 Ollama 原生 API 的两个核心聊天端点：
//   • /api/chat    - 多轮对话接口，支持消息历史
//   • /api/generate - 单次生成接口，支持 system + prompt 模式
//
// 主要职责：
//   1. 解析 Ollama 格式的请求（OllamaChatRequest / OllamaGenerateRequest）
//   2. 将模型名称从外部暴露名转换为内部名称
//   3. 构建统一的代理请求（AiProxyAgentRequest）
//   4. 调用后端服务执行聊天，支持流式和非流式响应
//   5. 将后端响应转换为 Ollama 格式返回给客户端
// =============================================================================

using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.Entitys.Proxy;

using System.Net;
using System.Text.Json;

namespace Netor.Cortana.Networks.Proxy;

/// <summary>
/// Ollama 原生聊天端点处理器，处理 /api/chat 和 /api/generate 请求。
/// </summary>
/// <remarks>
/// <para>
/// 该类作为 Ollama API 兼容层的核心处理器，负责将 Ollama 格式的请求转换为
/// 统一的代理请求格式，并调用后端 AI 服务完成实际的聊天任务。
/// </para>
/// <para>
/// 支持的端点：
/// <list type="bullet">
///   <item><description>/api/chat - 多轮对话接口，接收消息数组</description></item>
///   <item><description>/api/generate - 单次生成接口，接收 prompt 和可选 system</description></item>
/// </list>
/// </para>
/// <para>
/// 两种端点都支持流式（stream: true）和非流式（stream: false）响应模式。
/// </para>
/// </remarks>
public sealed class ProxyChatEndpoints(IServiceProvider services)
{
    /// <summary>
    /// 处理 /api/chat 端点请求，支持多轮对话。
    /// </summary>
    /// <param name="context">HTTP 监听器上下文，包含请求和响应对象。</param>
    /// <param name="options">代理配置快照，包含模式、提供商 ID、模型 ID 等信息。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <remarks>
    /// 处理流程：
    /// <list type="number">
    ///   <item><description>解析 OllamaChatRequest 请求体</description></item>
    ///   <item><description>验证请求有效性（model 非空、messages 非空）</description></item>
    ///   <item><description>转换模型名称并构建代理请求</description></item>
    ///   <item><description>调用后端执行聊天并返回响应</description></item>
    /// </list>
    /// </remarks>
    public async Task HandleChatAsync(
        HttpListenerContext context,
        AiProxyOptionsSnapshot options,
        CancellationToken cancellationToken)
    {
        // 步骤 1：解析请求体为 OllamaChatRequest 对象
        var request = await JsonSerializer.DeserializeAsync(
            context.Request.InputStream,
            OllamaProxyJsonContext.Default.OllamaChatRequest,
            cancellationToken);

        // 步骤 2：验证请求有效性
        if (request is null || string.IsNullOrWhiteSpace(request.Model))
        {
            await OllamaHttpResponseWriter.WriteErrorAsync(context.Response, 400, "invalid chat request", cancellationToken);
            return;
        }

        // 步骤 3：转换消息格式，提取 role 和 content
        var messages = request.Messages?.Select(m => new AiProxyMessage(m.Role, m.Content)).ToArray() ?? [];
        if (messages.Length == 0)
        {
            await OllamaHttpResponseWriter.WriteErrorAsync(context.Response, 400, "chat request messages cannot be empty", cancellationToken);
            return;
        }

        // 步骤 4：构建代理请求，转换模型名称（外部暴露名 → 内部名称）
        var exposedModel = request.Model;
        var proxyRequest = CreateProxyRequest(
            AiProxyRequestKind.Chat,
            ProxyModelNameMapper.ToInternalModelName(request.Model),
            messages,
            request.Stream ?? true,
            request.Options,
            options);

        // 步骤 5：执行代理聊天，传入原始模型名用于响应
        await ExecuteProxyChatAsync(context.Response, proxyRequest, exposedModel, isGenerate: false, cancellationToken);
    }

    /// <summary>
    /// 处理 /api/generate 端点请求，支持单次文本生成。
    /// </summary>
    /// <param name="context">HTTP 监听器上下文，包含请求和响应对象。</param>
    /// <param name="options">代理配置快照，包含模式、提供商 ID、模型 ID 等信息。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <remarks>
    /// <para>
    /// 与 /api/chat 不同，/api/generate 使用 prompt + system 模式而非消息数组。
    /// 该方法会将 system（如有）和 prompt 转换为消息格式后再处理。
    /// </para>
    /// <para>
    /// 处理流程：
    /// <list type="number">
    ///   <item><description>解析 OllamaGenerateRequest 请求体</description></item>
    ///   <item><description>验证请求有效性（model 非空）</description></item>
    ///   <item><description>将 system + prompt 转换为消息格式</description></item>
    ///   <item><description>构建代理请求并执行聊天</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public async Task HandleGenerateAsync(
        HttpListenerContext context,
        AiProxyOptionsSnapshot options,
        CancellationToken cancellationToken)
    {
        // 步骤 1：解析请求体为 OllamaGenerateRequest 对象
        var request = await JsonSerializer.DeserializeAsync(
            context.Request.InputStream,
            OllamaProxyJsonContext.Default.OllamaGenerateRequest,
            cancellationToken);

        // 步骤 2：验证请求有效性
        if (request is null || string.IsNullOrWhiteSpace(request.Model))
        {
            await OllamaHttpResponseWriter.WriteErrorAsync(context.Response, 400, "invalid generate request", cancellationToken);
            return;
        }

        // 步骤 3：构建消息列表，将 system + prompt 转换为消息格式
        var messages = new List<AiProxyMessage>();
        if (!string.IsNullOrWhiteSpace(request.System))
        {
            // 添加 system 消息（如有）
            messages.Add(new AiProxyMessage("system", request.System));
        }
        // 添加 user 消息（prompt 内容）
        messages.Add(new AiProxyMessage("user", request.Prompt ?? string.Empty));

        // 步骤 4：构建代理请求，转换模型名称
        var exposedModel = request.Model;
        var proxyRequest = CreateProxyRequest(
            AiProxyRequestKind.Generate,
            ProxyModelNameMapper.ToInternalModelName(request.Model),
            messages,
            request.Stream ?? true,
            request.Options,
            options);

        // 步骤 5：执行代理聊天，标记为 generate 模式
        await ExecuteProxyChatAsync(context.Response, proxyRequest, exposedModel, isGenerate: true, cancellationToken);
    }

    /// <summary>
    /// 执行代理聊天请求，处理流式和非流式响应。
    /// </summary>
    /// <param name="response">HTTP 响应对象，用于写入返回数据。</param>
    /// <param name="request">统一格式的代理请求。</param>
    /// <param name="responseModel">用于响应的模型名称（外部暴露名）。</param>
    /// <param name="isGenerate">是否为 /api/generate 端点请求，决定响应格式。</param>
    /// <param name="cancellationToken">用于取消异步操作的令牌。</param>
    /// <remarks>
    /// <para>
    /// 该方法根据请求的 Stream 属性决定响应模式：
    /// <list type="bullet">
    ///   <item><description>流式（stream: true）：逐块返回增量数据</description></item>
    ///   <item><description>非流式（stream: false）：等待完成后返回完整响应</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private async Task ExecuteProxyChatAsync(
        HttpListenerResponse response,
        AiProxyAgentRequest request,
        string responseModel,
        bool isGenerate,
        CancellationToken cancellationToken)
    {
        // 步骤 1：获取后端服务实例
        var backend = GetBackend();
        if (backend is null)
        {
            await OllamaHttpResponseWriter.WriteErrorAsync(response, 503, "proxy backend unavailable", cancellationToken);
            return;
        }

        // 步骤 2：调用后端执行聊天，获取增量数据流
        var deltas = backend.ChatAsync(request, cancellationToken);

        // 步骤 3：根据流式/非流式模式返回响应
        if (request.Stream)
        {
            // 流式模式：根据端点类型选择不同的写入器
            if (isGenerate)
                await OllamaHttpResponseWriter.WriteGenerateStreamAsync(response, responseModel, deltas, cancellationToken);
            else
                await OllamaHttpResponseWriter.WriteChatStreamAsync(response, responseModel, deltas, cancellationToken);
            return;
        }

        // 步骤 4：非流式模式：收集所有增量数据，构建完整响应
        var content = new System.Text.StringBuilder();
        AiProxyChatDelta? last = null;
        await foreach (var delta in deltas.WithCancellation(cancellationToken))
        {
            // 累积内容并记录最后一个增量（包含 token 统计）
            if (!string.IsNullOrEmpty(delta.Content)) content.Append(delta.Content);
            last = delta;
        }

        // 步骤 5：根据端点类型构建不同格式的响应
        if (isGenerate)
        {
            // /api/generate 响应格式
            var payload = new OllamaGenerateResponse(
                responseModel,
                OllamaHttpResponseWriter.FormatUtcNow(),
                content.ToString(),
                true,
                ToDoneReason(last?.FinishReason),
                TotalDuration: 0,
                LoadDuration: 0,
                PromptEvalCount: last?.InputTokens,
                PromptEvalDuration: 0,
                EvalCount: last?.OutputTokens,
                EvalDuration: 0);

            await OllamaHttpResponseWriter.WriteJsonAsync(response, 200, payload, OllamaProxyJsonContext.Default.OllamaGenerateResponse, cancellationToken);
        }
        else
        {
            // /api/chat 响应格式
            var payload = new OllamaChatResponse(
                responseModel,
                OllamaHttpResponseWriter.FormatUtcNow(),
                new OllamaMessage("assistant", content.ToString()),
                true,
                ToDoneReason(last?.FinishReason),
                TotalDuration: 0,
                LoadDuration: 0,
                PromptEvalCount: last?.InputTokens,
                PromptEvalDuration: 0,
                EvalCount: last?.OutputTokens,
                EvalDuration: 0);

            await OllamaHttpResponseWriter.WriteJsonAsync(response, 200, payload, OllamaProxyJsonContext.Default.OllamaChatResponse, cancellationToken);
        }
    }

    /// <summary>
    /// 创建统一格式的代理请求对象。
    /// </summary>
    /// <param name="kind">请求类型（Chat 或 Generate）。</param>
    /// <param name="model">内部模型名称（已转换）。</param>
    /// <param name="messages">消息列表。</param>
    /// <param name="stream">是否启用流式响应。</param>
    /// <param name="requestOptions">Ollama 请求选项（包含 temperature、top_p 等）。</param>
    /// <param name="options">代理配置快照。</param>
    /// <returns>构建完成的代理请求对象。</returns>
    /// <remarks>
    /// 该方法将 Ollama 特定的请求格式转换为统一的代理请求格式，
    /// 便于后端服务统一处理不同来源的请求。
    /// </remarks>
    private static AiProxyAgentRequest CreateProxyRequest(
        AiProxyRequestKind kind,
        string model,
        IReadOnlyList<AiProxyMessage> messages,
        bool stream,
        OllamaRequestOptions? requestOptions,
        AiProxyOptionsSnapshot options)
    {
        return new AiProxyAgentRequest(
            RequestId: Guid.NewGuid().ToString("N"),           // 生成唯一请求 ID
            SessionKey: null,                                   // 无会话键
            Kind: kind,                                         // 请求类型
            Mode: options.Mode,                                 // 代理模式
            Model: model,                                       // 内部模型名称
            Messages: messages,                                 // 消息列表
            Stream: stream,                                     // 流式开关
            ProviderId: options.ProviderId,                     // 提供商 ID
            ModelId: options.ModelId,                           // 模型 ID
            AgentId: options.AgentId,                           // 代理 ID
            Temperature: requestOptions?.Temperature,           // 温度参数
            MaxTokens: requestOptions?.NumPredict,             // 最大 token 数
            TopP: requestOptions?.TopP);                        // Top-P 采样参数
    }

    /// <summary>
    /// 从服务容器获取后端服务实例。
    /// </summary>
    /// <returns>后端服务实例，若未注册则返回 null。</returns>
    /// <remarks>
    /// 使用 IServiceProvider 动态获取后端服务，避免构造函数注入导致的循环依赖。
    /// </remarks>
    private IAiProxyAgentBackend? GetBackend()
    {
        return services.GetService(typeof(IAiProxyAgentBackend)) as IAiProxyAgentBackend;
    }

    /// <summary>
    /// 将代理完成原因转换为 Ollama 格式的字符串。
    /// </summary>
    /// <param name="reason">代理完成原因枚举值。</param>
    /// <returns>Ollama 格式的完成原因字符串，若未知则返回 null。</returns>
    /// <remarks>
    /// 映射关系：
    /// <list type="bullet">
    ///   <item><description>Stop → "stop"：正常完成</description></item>
    ///   <item><description>Length → "length"：达到最大长度限制</description></item>
    ///   <item><description>Cancelled → "cancelled"：用户取消</description></item>
    ///   <item><description>Error → "error"：发生错误</description></item>
    /// </list>
    /// </remarks>
    private static string? ToDoneReason(AiProxyFinishReason? reason) => reason switch
    {
        AiProxyFinishReason.Stop => "stop",
        AiProxyFinishReason.Length => "length",
        AiProxyFinishReason.Cancelled => "cancelled",
        AiProxyFinishReason.Error => "error",
        _ => null
    };
}