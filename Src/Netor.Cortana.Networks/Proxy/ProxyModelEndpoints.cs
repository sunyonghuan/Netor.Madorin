// =====================================================================
// 文件：ProxyModelEndpoints.cs
// 功能：模型相关端点处理器
// 说明：
//   处理 Ollama 风格的模型查询 API 端点，包括：
//   - /api/tags    ：列出所有可用模型（Ollama 原生格式）
//   - /api/show    ：显示指定模型的详细信息
//   - /v1/models   ：列出所有可用模型（OpenAI 兼容格式）
// =====================================================================

using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.Entitys.Proxy;

using System.Net;
using System.Text.Json;

namespace Netor.Cortana.Networks.Proxy;

/// <summary>
/// 模型相关端点处理器。
/// </summary>
/// <remarks>
/// <para>
/// 该类负责处理与模型查询相关的 HTTP 请求，提供 Ollama 原生格式和 OpenAI 兼容格式两种 API 风格。
/// </para>
/// <para>
/// 支持的端点：
/// <list type="bullet">
///   <item><description>/api/tags - 获取模型列表（Ollama 格式）</description></item>
///   <item><description>/api/show - 获取模型详情（Ollama 格式）</description></item>
///   <item><description>/v1/models - 获取模型列表（OpenAI 格式）</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class ProxyModelEndpoints(IServiceProvider services)
{
    /// <summary>
    /// 处理 /api/tags 端点请求，返回 Ollama 格式的模型列表。
    /// </summary>
    /// <param name="response">HTTP 响应对象，用于写入返回内容。</param>
    /// <param name="cancellationToken">取消令牌，用于取消异步操作。</param>
    /// <remarks>
    /// 返回格式示例：
    /// <code>
    /// {
    ///   "models": [
    ///     { "name": "llama3:latest", "modified_at": "2024-01-01T00:00:00Z", ... }
    ///   ]
    /// }
    /// </code>
    /// </remarks>
    public async Task HandleTagsAsync(HttpListenerResponse response, CancellationToken cancellationToken)
    {
        // Step 1: 获取后端服务实例
        var backend = GetBackend();
        if (backend is null)
        {
            // 后端不可用时返回 503 服务不可用错误
            await OllamaHttpResponseWriter.WriteErrorAsync(response, 503, "proxy backend unavailable", cancellationToken);
            return;
        }

        // Step 2: 获取当前时间戳，用于模型的 modified_at 字段
        var now = DateTimeOffset.Now;

        // Step 3: 从后端获取模型列表并转换为 Ollama 格式
        var models = backend.ListModels()
            .Select(m => OllamaModelShapeFactory.CreateTagModel(m, now))  // 转换为 Ollama 标签格式
            .ToArray();                                                    // 物化查询结果

        // Step 4: 写入 JSON 响应
        await OllamaHttpResponseWriter.WriteJsonAsync(
            response,
            200,  // HTTP 200 OK
            new OllamaTagsResponse(models),
            OllamaProxyJsonContext.Default.OllamaTagsResponse,  // 使用预生成的序列化上下文
            cancellationToken);
    }

    /// <summary>
    /// 处理 /api/show 端点请求，返回指定模型的详细信息。
    /// </summary>
    /// <param name="context">HTTP 上下文，包含请求和响应对象。</param>
    /// <param name="cancellationToken">取消令牌，用于取消异步操作。</param>
    /// <remarks>
    /// 该端点用于获取单个模型的详细信息，包括模型参数、模板、许可证等元数据。
    /// 如果请求中未指定模型名称，则使用默认模型。
    /// </remarks>
    public async Task HandleShowAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        // Step 1: 反序列化请求体，获取要查询的模型名称
        var request = await JsonSerializer.DeserializeAsync(
            context.Request.InputStream,
            OllamaProxyJsonContext.Default.OllamaShowRequest,
            cancellationToken);

        // Step 2: 确定模型名称（使用请求中的名称或默认名称）
        var modelName = request?.Model ?? ProxyModelNameMapper.ToExposedModelName("default");

        // Step 3: 创建模型详情响应对象
        var payload = OllamaModelShapeFactory.CreateShowResponse(modelName);

        // Step 4: 写入 JSON 响应
        await OllamaHttpResponseWriter.WriteJsonAsync(
            context.Response,
            200,  // HTTP 200 OK
            payload,
            OllamaProxyJsonContext.Default.OllamaShowResponse,
            cancellationToken);
    }

    /// <summary>
    /// 处理 /v1/models 端点请求，返回 OpenAI 格式的模型列表。
    /// </summary>
    /// <param name="response">HTTP 响应对象，用于写入返回内容。</param>
    /// <param name="cancellationToken">取消令牌，用于取消异步操作。</param>
    /// <remarks>
    /// <para>
    /// 该端点兼容 OpenAI API 规范，返回格式示例：
    /// </para>
    /// <code>
    /// {
    ///   "object": "list",
    ///   "data": [
    ///     { "id": "llama3", "object": "model", "created": 1704067200, "owned_by": "library" }
    ///   ]
    /// }
    /// </code>
    /// </remarks>
    public async Task HandleV1ModelsAsync(HttpListenerResponse response, CancellationToken cancellationToken)
    {
        // Step 1: 获取后端服务实例
        var backend = GetBackend();
        if (backend is null)
        {
            // 后端不可用时返回 503 服务不可用错误
            await OllamaHttpResponseWriter.WriteErrorAsync(response, 503, "proxy backend unavailable", cancellationToken);
            return;
        }

        // Step 2: 获取当前 Unix 时间戳（OpenAI 格式要求）
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Step 3: 从后端获取模型列表并转换为 OpenAI 格式
        var data = backend.ListModels()
            .Select(m => new OpenAiModelEntry(
                ProxyModelNameMapper.ToExposedModelName(m.Name),  // 转换为对外暴露的模型名称
                "model",                                           // 对象类型固定为 "model"
                now,                                               // 创建时间戳
                "library"))                                        // 所有者固定为 "library"
            .ToArray();                                            // 物化查询结果

        // Step 4: 写入 JSON 响应
        await OllamaHttpResponseWriter.WriteJsonAsync(
            response,
            200,  // HTTP 200 OK
            new OpenAiModelsResponse("list", data),
            OllamaProxyJsonContext.Default.OpenAiModelsResponse,  // 使用预生成的序列化上下文
            cancellationToken);
    }

    /// <summary>
    /// 从服务容器中获取 AI 代理后端实例。
    /// </summary>
    /// <returns>
    /// 如果后端服务已注册则返回实例，否则返回 <c>null</c>。
    /// </returns>
    /// <remarks>
    /// 使用 <see cref="IServiceProvider.GetService"/> 方法获取服务，
    /// 避免在构造函数中注入导致循环依赖问题。
    /// </remarks>
    private IAiProxyAgentBackend? GetBackend()
    {
        return services.GetService(typeof(IAiProxyAgentBackend)) as IAiProxyAgentBackend;
    }
}