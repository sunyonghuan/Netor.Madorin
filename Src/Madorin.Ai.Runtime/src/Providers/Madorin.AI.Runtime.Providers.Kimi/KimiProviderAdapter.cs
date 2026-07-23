using System.Collections.Concurrent;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Providers.Kimi.Models;
using Madorin.AI.Runtime.Providers.Kimi.Protocol;

namespace Madorin.AI.Runtime.Providers.Kimi;

/// <summary>
/// Kimi（Moonshot AI）专用 Provider Adapter。
/// 使用 OpenAI 兼容协议接入 Kimi 端点，
/// 通过 <see cref="KimiOverrideHandler"/> 自动注入 thinking 参数并补写 partial 字段。
/// 从老项目 <c>Netor.Cortana.AI/Drivers/Providers/Kimi/</c> 迁移。
/// </summary>
public sealed class KimiProviderAdapter : IRuntimeProviderAdapter
{
    private const string ProviderRequestFailed = nameof(ProviderRequestFailed);
    private const string ProviderRateLimited = nameof(ProviderRateLimited);

    private static readonly string DefaultBaseUrl = "https://api.moonshot.cn/v1";

    private readonly ConcurrentDictionary<string, IChatClient> _chatClients =
        new(StringComparer.Ordinal);

    private readonly Func<string, IChatClient> _chatClientFactory;

    public KimiProviderAdapter(
        string apiKey,
        string? baseUrl = null,
        ILoggerFactory? loggerFactory = null,
        IReadOnlyList<ProviderModel>? models = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var logFactory = loggerFactory
            ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        Models = models ?? KimiModelCatalog.All;

        // ── HTTP 管道：KimiOverrideHandler → 实际 HTTP 发送 ──────────────────
        var overrideHandler = new KimiOverrideHandler(
            logFactory.CreateLogger<KimiOverrideHandler>());
        overrideHandler.InnerHandler = new HttpClientHandler();
        var httpClient = new HttpClient(overrideHandler);

        var credential = new ApiKeyCredential(apiKey);
        var endpoint = new Uri((baseUrl ?? DefaultBaseUrl).TrimEnd('/'));
        var options = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            NetworkTimeout = TimeSpan.FromMinutes(10),
            Transport = new HttpClientPipelineTransport(httpClient),
            ClientLoggingOptions = new ClientLoggingOptions
            {
                EnableLogging = false,
                EnableMessageLogging = false,
                EnableMessageContentLogging = false,
                MessageContentSizeLimit = 1024
            }
        };

        var openAiClient = new OpenAIClient(credential, options);

        _chatClientFactory = modelId => _chatClients.GetOrAdd(
            modelId,
            id => openAiClient.GetChatClient(id).AsIChatClient());
    }

    public string ProviderId => "kimi";

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

        yield return new InvocationCompletedProviderEvent(request.InvocationId, finishReason);
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
}
