using System.Collections.Concurrent;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.DeepSeek.Models;
using Madorin.AI.Runtime.Providers.DeepSeek.Protocol;

namespace Madorin.AI.Runtime.Providers.DeepSeek;

/// <summary>
/// DeepSeek 专用 Provider Adapter。
/// 使用 OpenAI 兼容协议接入 DeepSeek 端点，
/// 通过 <see cref="DeepSeekOverrideHandler"/> 在 HTTP 层自动补写 reasoning_content，
/// 通过 <see cref="DeepSeekDelegatingChatClient"/> 在请求前缓存 reasoning 回传上下文。
/// 从老项目 <c>Netor.Cortana.AI/Drivers/Providers/Deepseek/</c> 迁移。
/// </summary>
public sealed class DeepSeekProviderAdapter : IRuntimeProviderAdapter, IDisposable
{
    private const string ProviderRequestFailed = nameof(ProviderRequestFailed);
    private const string ProviderRateLimited = nameof(ProviderRateLimited);

    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, IChatClient> _chatClients =
        new(StringComparer.Ordinal);

    private readonly HttpClient _httpClient;

    public DeepSeekProviderAdapter(
        string baseUrl,
        string apiKey,
        ILoggerFactory? loggerFactory = null,
        IReadOnlyList<ProviderModel>? models = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _loggerFactory = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        Models = models ?? DeepSeekModelCatalog.All;

        // ── HTTP 管道：DeepSeekOverrideHandler → 实际 HTTP 发送 ──────────────
        var overrideHandler = new DeepSeekOverrideHandler(
            _loggerFactory.CreateLogger<DeepSeekOverrideHandler>());
        overrideHandler.InnerHandler = new HttpClientHandler();
        _httpClient = new HttpClient(overrideHandler);

        var credential = new ApiKeyCredential(apiKey);
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(baseUrl.TrimEnd('/')),
            NetworkTimeout = TimeSpan.FromMinutes(10),
            Transport = new HttpClientPipelineTransport(_httpClient),
            ClientLoggingOptions = new ClientLoggingOptions
            {
                EnableLogging = false,
                EnableMessageLogging = false,
                EnableMessageContentLogging = false,
                MessageContentSizeLimit = 1024
            }
        };

        var openAiClient = new OpenAIClient(credential, options);

        // Factory：每个 modelId 复用同一 IChatClient 实例（含 DeepSeekDelegatingChatClient 包装）
        _chatClientFactory = modelId => _chatClients.GetOrAdd(
            modelId,
            id => new DeepSeekDelegatingChatClient(
                openAiClient.GetChatClient(id).AsIChatClient()));
    }

    private readonly Func<string, IChatClient> _chatClientFactory;

    public string ProviderId => "deepseek";

    public ProviderCapabilities Capabilities { get; } = new(
        Streaming: true,
        ToolCalling: true,
        Reasoning: true);

    public IReadOnlyList<ProviderModel> Models { get; }

    public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct = default)
        => ProviderCapabilityValidator.ValidateAsync(Capabilities, input, ct);

    public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
        RuntimeProviderRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            request.CancellationToken);
        var effectiveCt = linkedCts.Token;
        await ValidateCapabilitiesAsync(
            FlattenContent(request.Messages),
            effectiveCt).ConfigureAwait(false);
        var chatClient = _chatClientFactory(request.ModelId);
        var messages = ConvertMessages(request.Messages);
        var options = new ChatOptions
        {
            ModelId = request.ModelId,
            Temperature = request.Temperature,
            MaxOutputTokens = request.MaxTokens
        };

        string finishReason = "stop";
        RuntimeError? failure = null;

        await foreach (var update in chatClient.GetStreamingResponseAsync(messages, options, effectiveCt)
                           .ConfigureAwait(false))
        {
            if (update.FinishReason is { } reason)
            {
                finishReason = reason.ToString();
            }

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                        yield return new ReasoningDeltaProviderEvent(request.InvocationId, reasoning.Text);
                        break;

                    case TextContent text when !string.IsNullOrEmpty(text.Text):
                        yield return new TextDeltaProviderEvent(request.InvocationId, text.Text);
                        break;

                    case UsageContent usage when usage.Details is not null:
                        yield return new UsageUpdatedProviderEvent(
                            request.InvocationId,
                            (int)(usage.Details.InputTokenCount ?? 0),
                            (int)(usage.Details.OutputTokenCount ?? 0));
                        break;
                }
            }
        }

        if (failure is not null)
        {
            yield return new InvocationFailedProviderEvent(request.InvocationId, failure);
        }
        else
        {
            yield return new InvocationCompletedProviderEvent(request.InvocationId, finishReason);
        }
    }

    private static ChatMessage[] ConvertMessages(RuntimeProviderMessage[] source)
    {
        var messages = new List<ChatMessage>(source.Sum(static message => message.Content.Length));
        foreach (var message in source)
        {
            var role = ConvertRole(message.Role);
            foreach (var block in message.Content)
            {
                messages.Add(block switch
                {
                    TextContentBlock text => new ChatMessage(role, text.Text),
                    ReasoningContentBlock reasoning => new ChatMessage(
                        ChatRole.Assistant,
                        [new TextReasoningContent(reasoning.Content)]),
                    _ => new ChatMessage(role, block.ToString() ?? string.Empty)
                });
            }
        }

        return [.. messages];
    }

    private static ChatRole ConvertRole(string role) => role switch
    {
        RuntimeProviderRoles.System => ChatRole.System,
        RuntimeProviderRoles.User => ChatRole.User,
        RuntimeProviderRoles.Assistant => ChatRole.Assistant,
        RuntimeProviderRoles.Tool => ChatRole.Tool,
        _ => throw new InvalidOperationException($"Unsupported Provider message role '{role}'.")
    };

    private static ContentBlock[] FlattenContent(RuntimeProviderMessage[] messages) =>
        [.. messages.SelectMany(static message => message.Content)];

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
