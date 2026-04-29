// =====================================================================
// 文件：OpenAiCompatibleRawProxy.cs
// 功能：OpenAI 兼容协议原始透传代理客户端
// 说明：用于 /v1/chat/completions 等需要完整保留 tools/tool_calls/reasoning
//       字段的场景，实现请求转发与响应模型名重写
// =====================================================================

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Netor.Cortana.Networks.Proxy;

/// <summary>
/// OpenAI 兼容协议原始透传客户端。
/// <para>
/// 用于 /v1/chat/completions 这类需要完整保留 tools/tool_calls/reasoning 字段的场景。
/// 该类负责将客户端请求透传到上游 AI 服务提供商，并将响应中的内部模型名替换回对外暴露的模型名。
/// </para>
/// <para>
/// 主要功能：
/// <list type="bullet">
///   <item><description>解析配置的 AI 服务提供商</description></item>
///   <item><description>将外部模型名映射到内部模型名</description></item>
///   <item><description>转发 chat completions 请求到上游服务</description></item>
///   <item><description>重写响应中的模型名字段</description></item>
/// </list>
/// </para>
/// </summary>
/// <param name="providerService">AI 服务提供商服务，用于获取可用的提供商配置</param>
/// <param name="modelService">AI 模型服务，用于查询提供商下的模型信息</param>
/// <param name="settingsService">系统设置服务，用于读取代理相关配置</param>
public sealed class OpenAiCompatibleRawProxy(
    AiProviderService providerService,
    AiModelService modelService,
    SystemSettingsService settingsService)
{
    /// <summary>
    /// 共享的 HTTP 客户端实例。
    /// <para>
    /// 使用静态单例模式，避免每次请求都创建新的 HttpClient 实例，
    /// 防止端口耗尽问题。设置 10 分钟超时以适应大模型响应时间较长的特点。
    /// </para>
    /// </summary>
    private static readonly HttpClient SharedHttpClient = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>
    /// 转发 chat completions 请求到上游 AI 服务提供商。
    /// </summary>
    /// <param name="requestBody">请求体 JSON 数据流，包含完整的 chat completions 请求</param>
    /// <param name="toInternalModelName">
    /// 模型名转换函数，将对外暴露的模型名（如 cortana-gpt4）转换为内部实际使用的模型名
    /// </param>
    /// <param name="cancellationToken">取消令牌，用于取消异步操作</param>
    /// <returns>
    /// 原始代理结果，包含 HTTP 状态码、Content-Type 和响应体字节数组
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// 当没有可用的提供商配置、请求体无效或模型不存在时抛出
    /// </exception>
    public async Task<RawProxyResult> ForwardChatCompletionsAsync(
        Stream requestBody,
        Func<string, string> toInternalModelName,
        CancellationToken cancellationToken)
    {
        // Step 1: 解析配置的服务提供商
        var provider = ResolveConfiguredProvider()
            ?? throw new InvalidOperationException("没有可用的 Ollama Proxy 目标厂商。请先在代理设置中选择厂商，或设置系统默认厂商。");

        // Step 2: 解析请求体 JSON，获取请求参数
        using var document = await JsonDocument.ParseAsync(requestBody, cancellationToken: cancellationToken);
        var root = JsonNode.Parse(document.RootElement.GetRawText())?.AsObject()
            ?? throw new InvalidOperationException("invalid chat completions request");

        // Step 3: 获取并验证请求中的模型名
        var exposedModel = root["model"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(exposedModel))
        {
            throw new InvalidOperationException("invalid chat completions request: missing model");
        }

        // Step 4: 将外部模型名转换为内部模型名，并验证模型是否存在
        var internalModel = toInternalModelName(exposedModel);
        var model = ResolveModelInProvider(provider, internalModel)
            ?? throw new InvalidOperationException($"厂商 {provider.Name} 下找不到模型：{internalModel}");

        // Step 5: 替换请求体中的模型名为内部实际模型名
        root["model"] = model.Name;

        // Step 6: 构建上游请求 URL 并发送请求
        var upstreamUrl = BuildChatCompletionsUrl(provider.Url);
        using var request = new HttpRequestMessage(HttpMethod.Post, upstreamUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.Key);
        request.Content = new StringContent(root.ToJsonString(), Encoding.UTF8, "application/json");

        // Step 7: 发送请求并读取响应
        var response = await SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json; charset=utf-8";

        // Step 8: 将上游响应里的内部模型名替换回暴露给 VSCode 的 cortana-* 名称
        // 仅对 JSON 响应且非空响应体进行重写
        if (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase)
            && bytes.Length > 0)
        {
            bytes = RewriteResponseModel(bytes, exposedModel);
        }

        // Step 9: 返回代理结果
        return new RawProxyResult((int)response.StatusCode, contentType, bytes);
    }

    /// <summary>
    /// 解析配置的服务提供商。
    /// <para>
    /// 解析优先级：
    /// <list type="number">
    ///   <item><description>代理设置中指定的提供商（如果有效且已启用）</description></item>
    ///   <item><description>系统默认提供商</description></item>
    ///   <item><description>第一个可用提供商</description></item>
    /// </list>
    /// </para>
    /// </summary>
    /// <returns>解析到的提供商实体，如果没有可用提供商则返回 null</returns>
    private AiProviderEntity? ResolveConfiguredProvider()
    {
        // 获取所有提供商
        var providers = providerService.GetAll();
        if (providers.Count == 0) return null;

        // 尝试获取代理设置中指定的提供商
        var configuredProviderId = settingsService.GetValue("Proxy.Ollama.ProviderId", string.Empty);
        if (!string.IsNullOrWhiteSpace(configuredProviderId))
        {
            var configured = providerService.GetById(configuredProviderId);
            // 验证配置的提供商存在且已启用
            if (configured is not null && configured.IsEnabled) return configured;
        }

        // 回退到默认提供商或第一个可用提供商
        return providers.FirstOrDefault(p => p.IsDefault) ?? providers[0];
    }

    /// <summary>
    /// 在指定提供商下解析模型。
    /// <para>
    /// 使用标准化的模型名进行匹配，忽略大小写和 :latest 后缀差异。
    /// </para>
    /// </summary>
    /// <param name="provider">AI 服务提供商实体</param>
    /// <param name="externalModelName">外部模型名（用户请求中的模型名）</param>
    /// <returns>匹配的模型实体，如果未找到则返回 null</returns>
    private AiModelEntity? ResolveModelInProvider(AiProviderEntity provider, string externalModelName)
    {
        // 获取该提供商下的所有模型
        var models = modelService.GetByProviderId(provider.Id);
        if (models.Count == 0) return null;

        // 标准化模型名后进行匹配
        var normalized = NormalizeModelName(externalModelName);
        return models.FirstOrDefault(model =>
            string.Equals(NormalizeModelName(model.Name), normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 标准化模型名称。
    /// <para>
    /// 处理规则：
    /// <list type="bullet">
    ///   <item><description>去除首尾空白</description></item>
    ///   <item><description>移除 Ollama 风格的 :latest 后缀</description></item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="value">原始模型名</param>
    /// <returns>标准化后的模型名</returns>
    /// <example>
    /// <code>
    /// NormalizeModelName("llama3:latest") // 返回 "llama3"
    /// NormalizeModelName("  gpt-4  ")     // 返回 "gpt-4"
    /// </code>
    /// </example>
    private static string NormalizeModelName(string value)
    {
        var name = value.Trim();
        // 移除 Ollama 风格的 :latest 后缀
        if (name.EndsWith(":latest", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^":latest".Length];
        }
        return name;
    }

    /// <summary>
    /// 构建 chat completions API 的完整 URL。
    /// <para>
    /// 自动处理基础 URL 的格式：
    /// <list type="bullet">
    ///   <item><description>如果基础 URL 已包含 /v1，则直接追加 /chat/completions</description></item>
    ///   <item><description>否则追加 /v1/chat/completions</description></item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="baseUrl">服务提供商的基础 URL</param>
    /// <returns>完整的 chat completions API URL</returns>
    /// <example>
    /// <code>
    /// BuildChatCompletionsUrl("https://api.openai.com")      // 返回 "https://api.openai.com/v1/chat/completions"
    /// BuildChatCompletionsUrl("https://api.openai.com/v1")   // 返回 "https://api.openai.com/v1/chat/completions"
    /// </code>
    /// </example>
    private static string BuildChatCompletionsUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("provider url cannot be empty");
        }

        // 先全局压缩重复 v1 段，覆盖：
        // https://host/v1/v1
        // https://host/v1/v1/chat/completions
        // https://host/v1/v1/v1/chat/completions
        while (trimmed.Contains("/v1", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Replace("/v1", "/", StringComparison.OrdinalIgnoreCase);
        }

        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed + "/chat/completions";
        }

        return trimmed + "/chat/completions";
    }

    /// <summary>
    /// 重写响应中的模型名字段。
    /// <para>
    /// 将上游响应中的内部模型名替换为对外暴露的模型名，
    /// 使客户端（如 VSCode）能够正确识别响应来源。
    /// </para>
    /// </summary>
    /// <param name="bytes">原始响应体字节数组</param>
    /// <param name="exposedModel">对外暴露的模型名</param>
    /// <returns>重写后的响应体字节数组；如果解析失败则返回原始字节数组</returns>
    private static byte[] RewriteResponseModel(byte[] bytes, string exposedModel)
    {
        try
        {
            // 解析响应 JSON
            var node = JsonNode.Parse(bytes)?.AsObject();
            if (node is null) return bytes;

            // 替换 model 字段为对外暴露的模型名
            node["model"] = exposedModel;

            // 序列化回字节数组
            return Encoding.UTF8.GetBytes(node.ToJsonString());
        }
        catch
        {
            // 解析失败时返回原始响应，确保不中断请求
            return bytes;
        }
    }
}

/// <summary>
/// 原始代理结果记录类型。
/// <para>
/// 封装 HTTP 代理响应的核心信息，用于将上游响应原样返回给客户端。
/// </para>
/// </summary>
/// <param name="StatusCode">HTTP 状态码（如 200、400、500 等）</param>
/// <param name="ContentType">响应的 Content-Type 头（如 "application/json; charset=utf-8"）</param>
/// <param name="Body">响应体字节数组</param>
public sealed record RawProxyResult(int StatusCode, string ContentType, byte[] Body);