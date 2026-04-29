using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Drivers;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Proxy;
using Netor.Cortana.Entitys.Services;

using System.Runtime.CompilerServices;

namespace Netor.Cortana.AI.Proxys;

/// <summary>
/// Cortana Ollama Proxy 协议桥接后端。
/// 该实现只做外部 Ollama 协议到当前选中 AI 厂商模型的转发，不引入智能体概念，
/// 不复用、不读取、不写入主聊天窗口会话历史。
/// </summary>
public sealed class CortanaOllamaProxyAgentBackend(
    AiProviderService providerService,
    AiModelService modelService,
    SystemSettingsService settingsService,
    AiProviderDriverRegistry driverRegistry,
    ProxyUsageTracker usageTracker,
    ILogger<CortanaOllamaProxyAgentBackend> logger)
    : AiProxyAgentBackendBase(usageTracker)
{
    public override IReadOnlyList<AiProxyModelDescriptor> ListModels()
    {
        var provider = ResolveConfiguredProvider();
        if (provider is null) return [];

        return modelService.GetByProviderId(provider.Id)
            .Select(model => new AiProxyModelDescriptor(
                BuildExternalModelName(model),
                provider.Id,
                model.Id,
                string.IsNullOrWhiteSpace(model.DisplayName) ? model.Name : model.DisplayName,
                model.ContextLength,
                provider.Name,
                model.Name))
            .ToArray();
    }

    protected override async IAsyncEnumerable<AiProxyChatDelta> ChatCoreAsync(
        AiProxyAgentRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var provider = ResolveConfiguredProvider()
            ?? throw new InvalidOperationException("没有可用的 Ollama Proxy 目标厂商。请先在代理设置中选择厂商，或设置系统默认厂商。");

        var model = ResolveModelInProvider(provider, request.Model)
            ?? throw new InvalidOperationException($"厂商 {provider.Name} 下找不到模型：{request.Model}");

        logger.LogInformation(
            "Ollama Proxy 转发请求：RequestId={RequestId}, Provider={Provider}, Model={Model}",
            request.RequestId,
            provider.Name,
            model.Name);

        var driver = driverRegistry.Resolve(provider);
        using var chatClient = driver.CreateChatClient(provider, model);
        var options = BuildBridgeChatOptions(driver, provider, request);
        var messages = ConvertMessages(request.Messages);

        var emitted = false;
        await foreach (var update in chatClient.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            var text = ExtractText(update);
            if (string.IsNullOrEmpty(text)) continue;

            emitted = true;
            yield return new AiProxyChatDelta(text);
        }

        if (!emitted)
        {
            var response = await chatClient.GetResponseAsync(messages, options, cancellationToken);
            var text = response.Text ?? string.Empty;
            if (!string.IsNullOrEmpty(text))
            {
                yield return new AiProxyChatDelta(text);
            }

            yield return new AiProxyChatDelta(
                string.Empty,
                Done: true,
                FinishReason: AiProxyFinishReason.Stop,
                InputTokens: response.Usage?.InputTokenCount,
                OutputTokens: response.Usage?.OutputTokenCount);
            yield break;
        }

        yield return new AiProxyChatDelta(string.Empty, Done: true, FinishReason: AiProxyFinishReason.Stop);
    }

    private AiProviderEntity? ResolveConfiguredProvider()
    {
        var providers = providerService.GetAll();
        if (providers.Count == 0) return null;

        var configuredProviderId = settingsService.GetValue("Proxy.Ollama.ProviderId", string.Empty);
        if (!string.IsNullOrWhiteSpace(configuredProviderId))
        {
            var configured = providerService.GetById(configuredProviderId);
            if (configured is not null && configured.IsEnabled) return configured;
        }

        return providers.FirstOrDefault(p => p.IsDefault) ?? providers[0];
    }

    private AiModelEntity? ResolveModelInProvider(AiProviderEntity provider, string externalModelName)
    {
        var models = modelService.GetByProviderId(provider.Id);
        if (models.Count == 0) return null;

        if (string.IsNullOrWhiteSpace(externalModelName)
            || string.Equals(externalModelName, "cortana/default:latest", StringComparison.OrdinalIgnoreCase))
        {
            return models.FirstOrDefault(m => m.IsDefault) ?? models[0];
        }

        var normalized = NormalizeModelName(externalModelName);
        return models.FirstOrDefault(model =>
            string.Equals(NormalizeModelName(model.Name), normalized, StringComparison.OrdinalIgnoreCase)
            || string.Equals(NormalizeModelName(BuildExternalModelName(model)), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static ChatOptions BuildBridgeChatOptions(
        IAiProviderDriver driver,
        AiProviderEntity provider,
        AiProxyAgentRequest request)
    {
        var bridgeAgent = new AgentEntity
        {
            Name = "Ollama Protocol Bridge",
            Instructions = string.Empty,
            Temperature = request.Temperature ?? 0.7,
            TopP = request.TopP ?? 1.0,
            MaxTokens = request.MaxTokens ?? 0,
            FrequencyPenalty = 0,
            PresencePenalty = 0,
            MaxHistoryMessages = 0,
            IsEnabled = true
        };

        return driver.BuildChatOptions(provider, bridgeAgent);
    }

    private static List<ChatMessage> ConvertMessages(IReadOnlyList<AiProxyMessage> messages)
    {
        var result = new List<ChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            result.Add(new ChatMessage(ToChatRole(message.Role), message.Content ?? string.Empty));
        }
        return result;
    }

    private static ChatRole ToChatRole(string role) => role.ToLowerInvariant() switch
    {
        "system" => ChatRole.System,
        "assistant" => ChatRole.Assistant,
        "tool" => ChatRole.Tool,
        _ => ChatRole.User
    };

    private static string ExtractText(ChatResponseUpdate update)
    {
        if (!string.IsNullOrEmpty(update.Text)) return update.Text;
        if (update.Contents is null || update.Contents.Count == 0) return string.Empty;

        var parts = new List<string>();
        foreach (var content in update.Contents)
        {
            if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
            {
                parts.Add(text.Text);
            }
        }

        return parts.Count == 0 ? string.Empty : string.Concat(parts);
    }

    private static string BuildExternalModelName(AiModelEntity model)
    {
        return model.Name.Trim();
        //return name.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? name : $"{name}:latest";
    }

    private static string NormalizeModelName(string value)
    {
        var name = value.Trim();
        if (name.EndsWith(":latest", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^":latest".Length];
        }
        return name;
    }
}
