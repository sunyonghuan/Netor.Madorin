using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Drivers;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.ModelCapability;
using Netor.Cortana.Entitys.Services;

using System.Collections.Concurrent;
using System.Diagnostics;

namespace Netor.Cortana.AI.Providers;

/// <summary>
/// 基于插件授权配置向插件提供受控的大模型调用能力。
/// </summary>
/// <param name="settingsService">系统设置服务，用于读取插件模型能力授权配置。</param>
/// <param name="providerService">AI 厂商配置服务。</param>
/// <param name="modelService">AI 模型配置服务。</param>
/// <param name="driverRegistry">AI 厂商驱动注册表。</param>
/// <param name="logger">日志记录器。</param>
public sealed class PluginModelCapabilityService(
    SystemSettingsService settingsService,
    AiProviderService providerService,
    AiModelService modelService,
    AiProviderDriverRegistry driverRegistry,
    ILogger<PluginModelCapabilityService> logger) : IPluginModelCapabilityService
{
    private const string LlmCapability = "llm";
    private const int DefaultTimeoutMs = 120000;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _concurrencyLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 执行插件发起的模型能力调用，并返回宿主模型输出。
    /// </summary>
    /// <param name="request">模型能力调用请求。</param>
    /// <param name="cancellationToken">取消调用的令牌。</param>
    /// <returns>模型能力调用响应。</returns>
    /// <exception cref="InvalidOperationException">请求无效、授权模型缺失或关联厂商不可用时抛出。</exception>
    /// <exception cref="UnauthorizedAccessException">插件未被授权使用大模型能力时抛出。</exception>
    /// <exception cref="TimeoutException">调用超时或并发限流等待超时时抛出。</exception>
    public async Task<ModelCapabilityResponse> InvokeAsync(ModelCapabilityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.Type, "request", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("不支持的 model-capability 请求。 ");
        }

        if (string.IsNullOrWhiteSpace(request.PluginId))
        {
            throw new InvalidOperationException("插件 ID 不能为空。 ");
        }

        var settings = ReadSettings(request.PluginId);
        if (!settings.Enabled)
        {
            throw new UnauthorizedAccessException("插件未授权使用大模型。 ");
        }

        var model = ResolveModel(settings)
            ?? throw new InvalidOperationException("插件授权未绑定有效模型。 ");
        var provider = providerService.GetById(model.ProviderId)
            ?? throw new InvalidOperationException("插件授权模型所属厂商不存在。 ");
        if (!provider.IsEnabled)
        {
            throw new InvalidOperationException("插件授权模型所属厂商未启用。 ");
        }

        var effectiveTimeout = GetEffectiveTimeout(request.TimeoutMs, settings.TimeoutMs);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(effectiveTimeout));

        var throttle = _concurrencyLocks.GetOrAdd(request.PluginId, _ => new SemaphoreSlim(settings.MaxConcurrency, settings.MaxConcurrency));
        if (!await throttle.WaitAsync(TimeSpan.FromMilliseconds(effectiveTimeout), timeoutCts.Token).ConfigureAwait(false))
        {
            throw new TimeoutException("插件大模型授权并发已满，请稍后重试。 ");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var driver = driverRegistry.Resolve(provider);
            using var client = driver.CreateChatClient(provider, model);
            var messages = BuildMessages(request);
            var options = BuildOptions(driver, provider);
            var response = await client.GetResponseAsync(messages, options, timeoutCts.Token).ConfigureAwait(false);

            stopwatch.Stop();
            return new ModelCapabilityResponse
            {
                RequestId = request.RequestId,
                Success = true,
                Content = response.Text ?? string.Empty
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("插件大模型调用超时。 ");
        }
        catch (Exception ex) when (ex is not UnauthorizedAccessException and not InvalidOperationException and not TimeoutException)
        {
            logger.LogWarning(ex, "插件大模型调用失败：PluginId={PluginId}, Purpose={Purpose}", request.PluginId, request.Purpose);
            throw;
        }
        finally
        {
            throttle.Release();
        }
    }

    /// <summary>
    /// 读取指定插件的大模型能力授权与配额配置。
    /// </summary>
    /// <param name="pluginId">插件标识。</param>
    /// <returns>插件大模型能力设置。</returns>
    private PluginLlmSettings ReadSettings(string pluginId)
    {
        var prefix = $"Plugin:{pluginId}:Capability:{LlmCapability}";
        return new PluginLlmSettings(
            settingsService.GetValue($"{prefix}:Enabled", false),
            settingsService.GetValue($"{prefix}:ProviderId", string.Empty),
            settingsService.GetValue($"{prefix}:ModelId", string.Empty),
            settingsService.GetValue($"{prefix}:MaxInputTokens", 128000),
            settingsService.GetValue($"{prefix}:MaxOutputTokens", 128000),
            NormalizeTimeout(settingsService.GetValue($"{prefix}:TimeoutMs", DefaultTimeoutMs)),
            Math.Max(1, settingsService.GetValue($"{prefix}:MaxConcurrency", 3)),
            settingsService.GetValue($"{prefix}:AllowBackground", true));
    }

    /// <summary>
    /// 根据插件配置解析实际使用的模型，未指定时回退到默认厂商与默认模型。
    /// </summary>
    /// <param name="settings">插件大模型能力设置。</param>
    /// <returns>可用模型实体；未找到时返回 <see langword="null"/>。</returns>
    private AiModelEntity? ResolveModel(PluginLlmSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ModelId))
        {
            var configured = modelService.GetById(settings.ModelId);
            if (configured is not null && configured.IsEnabled) return configured;
        }

        if (!string.IsNullOrWhiteSpace(settings.ProviderId))
        {
            var models = modelService.GetByProviderId(settings.ProviderId);
            return models.FirstOrDefault(model => model.IsDefault) ?? models.FirstOrDefault();
        }

        var provider = providerService.GetAll().FirstOrDefault(item => item.IsDefault) ?? providerService.GetAll().FirstOrDefault();
        if (provider is null) return null;

        var providerModels = modelService.GetByProviderId(provider.Id);
        return providerModels.FirstOrDefault(model => model.IsDefault) ?? providerModels.FirstOrDefault();
    }

    /// <summary>
    /// 将模型能力请求转换为通用聊天消息列表。
    /// </summary>
    /// <param name="request">模型能力调用请求。</param>
    /// <returns>发送给聊天客户端的消息列表。</returns>
    private static List<ChatMessage> BuildMessages(ModelCapabilityRequest request)
    {
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(request.Instruction))
        {
            messages.Add(new ChatMessage(ChatRole.System, request.Instruction));
        }

        messages.Add(new ChatMessage(ChatRole.User, request.Input ?? string.Empty));
        return messages;
    }

    /// <summary>
    /// 根据插件配额与请求参数构建聊天选项。
    /// </summary>
    /// <param name="driver">当前厂商对应的 AI 驱动。</param>
    /// <param name="provider">当前使用的 AI 厂商配置。</param>
    /// <returns>聊天客户端调用选项。</returns>
    private static ChatOptions BuildOptions(
        IAiProviderDriver driver,
        AiProviderEntity provider)
    {
        var agent = new AgentEntity
        {
            Name = "Plugin Model Capability",
            Instructions = string.Empty,
            IsEnabled = true
        };

        return driver.BuildChatOptions(provider, agent);
    }

    /// <summary>
    /// 计算本次调用的实际超时时间。
    /// </summary>
    /// <param name="requestTimeoutMs">请求指定的超时时间，单位为毫秒。</param>
    /// <param name="configuredTimeoutMs">插件授权配置中的超时时间，单位为毫秒。</param>
    /// <returns>请求与配置共同限制后的超时时间。</returns>
    private static int GetEffectiveTimeout(int requestTimeoutMs, int configuredTimeoutMs)
    {
        var configured = NormalizeTimeout(configuredTimeoutMs);
        return requestTimeoutMs <= 0 ? configured : Math.Min(requestTimeoutMs, configured);
    }

    /// <summary>
    /// 标准化超时时间，过小或无效的配置回退到默认超时。
    /// </summary>
    /// <param name="timeoutMs">待标准化的超时时间，单位为毫秒。</param>
    /// <returns>有效的超时时间。</returns>
    private static int NormalizeTimeout(int timeoutMs)
    {
        return timeoutMs <= 30000 ? DefaultTimeoutMs : timeoutMs;
    }

    /// <summary>
    /// 插件大模型能力授权、模型绑定与调用配额设置。
    /// </summary>
    /// <param name="Enabled">指示插件是否被授权使用大模型能力。</param>
    /// <param name="ProviderId">授权绑定的 AI 厂商标识。</param>
    /// <param name="ModelId">授权绑定的 AI 模型标识。</param>
    /// <param name="MaxInputTokens">最大输入 Token 数。</param>
    /// <param name="MaxOutputTokens">最大输出 Token 数。</param>
    /// <param name="TimeoutMs">调用超时时间，单位为毫秒。</param>
    /// <param name="MaxConcurrency">插件级最大并发调用数。</param>
    /// <param name="AllowBackground">指示是否允许后台调用。</param>
    private sealed record PluginLlmSettings(
        bool Enabled,
        string ProviderId,
        string ModelId,
        int MaxInputTokens,
        int MaxOutputTokens,
        int TimeoutMs,
        int MaxConcurrency,
        bool AllowBackground);
}
