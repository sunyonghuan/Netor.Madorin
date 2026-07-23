using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Tools.Abstractions;
using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAI;
using OpenAI.Responses;

namespace Madorin.AI.Runtime.Providers.OpenAI;

public sealed class OpenAIProviderAdapter :
    IRuntimeProviderAdapter,
    IRuntimeProviderCredentialUpdater,
    IDisposable
{
    private readonly Func<string, IChatClient> _chatClientFactory;
    private readonly ConcurrentDictionary<string, IChatClient> _chatClients =
        new(StringComparer.Ordinal);
    private readonly IProviderBlobResolver? _blobResolver;
    private readonly ApiKeyCredential? _credential;
    private readonly HttpClient? _ownedHttpClient;

    public OpenAIProviderAdapter(
        IChatClient chatClient,
        IReadOnlyList<ProviderModel>? models = null,
        IProviderBlobResolver? blobResolver = null)
        : this(_ => chatClient, models, blobResolver)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
    }

    public OpenAIProviderAdapter(
        Func<string, IChatClient> chatClientFactory,
        IReadOnlyList<ProviderModel>? models = null,
        IProviderBlobResolver? blobResolver = null)
    {
        ArgumentNullException.ThrowIfNull(chatClientFactory);
        _chatClientFactory = chatClientFactory;
        _blobResolver = blobResolver;
        ProviderId = "openai";
        Models = models ?? [];
    }

    public OpenAIProviderAdapter(
        string apiKey,
        string baseUrl,
        IReadOnlyList<ProviderModel>? models = null,
        IProviderBlobResolver? blobResolver = null)
        : this(
            "openai",
            apiKey,
            baseUrl,
            ProviderHttpClientFactory.Shared.CreateClient("openai"),
            disposeHttpClient: true,
            models,
            blobResolver)
    {
    }

    public OpenAIProviderAdapter(
        string providerId,
        string apiKey,
        string baseUrl,
        IReadOnlyList<ProviderModel>? models,
        IProviderBlobResolver? blobResolver = null)
        : this(
            providerId,
            apiKey,
            baseUrl,
            ProviderHttpClientFactory.Shared.CreateClient(providerId),
            disposeHttpClient: true,
            models,
            blobResolver)
    {
    }

    public OpenAIProviderAdapter(
        string apiKey,
        string baseUrl,
        HttpClient httpClient,
        IReadOnlyList<ProviderModel>? models = null,
        IProviderBlobResolver? blobResolver = null)
        : this("openai", apiKey, baseUrl, httpClient, disposeHttpClient: false, models, blobResolver)
    {
    }

    public OpenAIProviderAdapter(
        string providerId,
        string apiKey,
        string baseUrl,
        IHttpClientFactory httpClientFactory,
        IReadOnlyList<ProviderModel>? models = null,
        IProviderBlobResolver? blobResolver = null)
        : this(
            providerId,
            apiKey,
            baseUrl,
            httpClientFactory.CreateClient(providerId),
            disposeHttpClient: true,
            models,
            blobResolver)
    {
    }

    private OpenAIProviderAdapter(
        string providerId,
        string apiKey,
        string baseUrl,
        HttpClient httpClient,
        bool disposeHttpClient,
        IReadOnlyList<ProviderModel>? models,
        IProviderBlobResolver? blobResolver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentNullException.ThrowIfNull(httpClient);

        _credential = new ApiKeyCredential(apiKey);
        _blobResolver = blobResolver;
        _ownedHttpClient = new HttpClient(
            new ProviderHttpResponseHandler(
                new HttpClientForwardingHandler(httpClient, disposeHttpClient)),
            disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        ProviderId = providerId;
        Models = models ?? [];

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(baseUrl.TrimEnd('/'), UriKind.Absolute),
            NetworkTimeout = TimeSpan.FromMinutes(10),
            RetryPolicy = new ClientRetryPolicy(0),
            Transport = new HttpClientPipelineTransport(_ownedHttpClient),
            ClientLoggingOptions = new ClientLoggingOptions
            {
                EnableLogging = false,
                EnableMessageLogging = false,
                EnableMessageContentLogging = false,
                MessageContentSizeLimit = 1024
            }
        };
        var responsesClient = new ResponsesClient(_credential, options);
        _chatClientFactory = modelId =>
            responsesClient.AsIChatClientWithStoredOutputDisabled(modelId);
    }

    public string ProviderId { get; }

    public ProviderCapabilities Capabilities { get; } = new(
        Streaming: true,
        ToolCalling: true,
        Vision: true,
        StructuredOutput: true,
        Reasoning: true,
        Files: true,
        Usage: true,
        RemoteCancellation: false);

    public IReadOnlyList<ProviderModel> Models { get; }

    public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct = default)
    {
        return ProviderCapabilityValidator.ValidateAsync(Capabilities, input, ct);
    }

    public Task UpdateCredentialsAsync(
        CredentialsUpdateParameters parameters,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ct.ThrowIfCancellationRequested();
        if (!string.Equals(parameters.ProviderId, ProviderId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Credential update targets another Provider.", nameof(parameters));
        }

        if (_credential is null)
        {
            throw new InvalidOperationException(
                "Credentials are owned by the injected OpenAI chat client and cannot be updated here.");
        }

        _credential.Update(parameters.Credential);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
        RuntimeProviderRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            ct,
            request.CancellationToken);
        var effectiveCt = linkedCts.Token;

        RuntimeError? failure = null;
        try
        {
            await ProviderCapabilityValidator.ValidateAsync(
                Capabilities,
                request,
                Models,
                effectiveCt).ConfigureAwait(false);
        }
        catch (RuntimeProviderException ex)
        {
            failure = ex.Error;
        }

        if (failure is not null)
        {
            yield return new InvocationFailedProviderEvent(request.InvocationId, failure);
            yield break;
        }

        foreach (var adjustment in GetProjectionAdjustments(request))
        {
            yield return adjustment;
        }

        IAsyncEnumerator<AgentResponseUpdate>? enumerator = null;
        try
        {
            var messages = await ConvertMessagesAsync(request.Messages, effectiveCt).ConfigureAwait(false);
            enumerator = CreateResponseStream(request, messages, effectiveCt).GetAsyncEnumerator(effectiveCt);
        }
        catch (OperationCanceledException) when (effectiveCt.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            failure = CreateProviderError(ex);
        }

        if (failure is not null || enumerator is null)
        {
            yield return new InvocationFailedProviderEvent(
                request.InvocationId,
                failure ?? CreateProviderError(new InvalidOperationException("Provider stream was not created.")));
            yield break;
        }

        string? finishReason = null;
        var hasUsage = false;
        await using var responseEnumerator = enumerator;

        while (true)
        {
            AgentResponseUpdate update;
            try
            {
                if (!await responseEnumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                update = responseEnumerator.Current;
            }
            catch (OperationCanceledException) when (effectiveCt.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failure = CreateProviderError(ex);
                break;
            }

            if (update.FinishReason is { } currentFinishReason)
            {
                finishReason = currentFinishReason.ToString();
            }

            foreach (var providerEvent in MapUpdate(request, update))
            {
                hasUsage |= providerEvent is UsageUpdatedProviderEvent;
                yield return providerEvent;
            }
        }

        if (failure is null && finishReason is null)
        {
            failure = ProviderErrorFactory.Create(
                "OpenAI",
                new InvalidDataException("Provider stream ended before a terminal update."),
                isProtocolError: true);
        }

        if (failure is not null)
        {
            yield return new InvocationFailedProviderEvent(request.InvocationId, failure);
            yield break;
        }

        if (!hasUsage)
        {
            yield return new UsageUpdatedProviderEvent(
                request.InvocationId,
                InputTokens: null,
                OutputTokens: null,
                ProviderUsageAccuracy.Unknown);
        }

        yield return new InvocationCompletedProviderEvent(request.InvocationId, finishReason!);
    }

    private IAsyncEnumerable<AgentResponseUpdate> CreateResponseStream(
        RuntimeProviderRequest request,
        ChatMessage[] messages,
        CancellationToken ct)
    {
        var chatClient = _chatClients.GetOrAdd(
            request.ModelId,
            modelId => _chatClientFactory(modelId)
                ?? throw new InvalidOperationException("The OpenAI chat client factory returned null."));
        var options = CreateChatOptions(request);

        var agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Id = request.AgentId,
            ChatOptions = options,
            UseProvidedChatClientAsIs = true
        });

        return agent.RunStreamingAsync(messages, cancellationToken: ct);
    }

    private static ChatOptions CreateChatOptions(RuntimeProviderRequest request)
    {
        return new ChatOptions
        {
            ModelId = request.ModelId,
            Temperature = request.Temperature,
            MaxOutputTokens = request.MaxTokens,
            Tools = CreateTools(request.Tools),
            ResponseFormat = CreateResponseFormat(request.StructuredOutput)
        };
    }

    private static ChatResponseFormatJson? CreateResponseFormat(RuntimeStructuredOutput? structuredOutput)
    {
        return structuredOutput is null
            ? null
            : ChatResponseFormat.ForJsonSchema(
                structuredOutput.Schema,
                structuredOutput.Name,
                structuredOutput.Description);
    }

    private static List<AITool>? CreateTools(ToolDescriptor[]? tools)
    {
        if (tools is not { Length: > 0 })
        {
            return null;
        }

        var declarations = new List<AITool>(tools.Length);
        foreach (var tool in tools)
        {
            using var schema = JsonDocument.Parse(tool.InputSchemaJson);
            declarations.Add(AIFunctionFactory.CreateDeclaration(
                tool.DisplayName,
                tool.Description,
                schema.RootElement.Clone()));
        }

        return declarations;
    }

    private async ValueTask<ChatMessage[]> ConvertMessagesAsync(
        RuntimeProviderMessage[] source,
        CancellationToken ct)
    {
        var messages = new List<ChatMessage>(source.Length);

        foreach (var message in source)
        {
            ct.ThrowIfCancellationRequested();
            var content = new List<AIContent>(message.Content.Length);
            foreach (var block in message.Content)
            {
                switch (block)
                {
                    case TextContentBlock text:
                        content.Add(new TextContent(text.Text));
                        break;
                    case ReasoningContentBlock reasoning:
                        content.Add(new TextReasoningContent(reasoning.Content)
                        {
                            ProtectedData = ProviderExtensionPolicy.GetReasoningSignature(reasoning, "openai")
                        });
                        break;
                    case ToolCallContentBlock toolCall:
                        content.Add(new FunctionCallContent(
                        toolCall.CallId,
                        toolCall.Name,
                        ConvertArguments(toolCall.Arguments)));
                        break;
                    case ToolResultContentBlock toolResult:
                        content.Add(new FunctionResultContent(
                        toolResult.CallId,
                        ConvertToolResult(toolResult)));
                        break;
                    case BlobRefContentBlock blobReference:
                        content.Add(await ResolveBlobAsync(blobReference.Blob, ct).ConfigureAwait(false));
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unsupported content block '{block.GetType().Name}'.");
                }
            }

            messages.Add(new ChatMessage(ConvertRole(message.Role), content));
        }

        return [.. messages];
    }

    private async ValueTask<DataContent> ResolveBlobAsync(
        BlobReference blob,
        CancellationToken ct)
    {
        if (_blobResolver is null)
        {
            throw new InvalidOperationException(
                $"Blob '{blob.BlobId}' cannot be sent because no Provider Blob resolver is configured.");
        }

        var resolved = await _blobResolver.ResolveAsync(blob, ct).ConfigureAwait(false);
        if (resolved.Data.Length != blob.Length)
        {
            throw new InvalidDataException($"Blob '{blob.BlobId}' length does not match its descriptor.");
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(resolved.Data.Span)).ToLowerInvariant();
        if (!string.Equals(sha256, blob.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Blob '{blob.BlobId}' SHA-256 does not match its descriptor.");
        }

        if (!string.Equals(resolved.ContentType, blob.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Blob '{blob.BlobId}' content type does not match its descriptor.");
        }

        return new DataContent(resolved.Data, resolved.ContentType)
        {
            Name = resolved.FileName
        };
    }

    private static ChatRole ConvertRole(string role) => role switch
    {
        RuntimeProviderRoles.System => ChatRole.System,
        RuntimeProviderRoles.User => ChatRole.User,
        RuntimeProviderRoles.Assistant => ChatRole.Assistant,
        RuntimeProviderRoles.Tool => ChatRole.Tool,
        _ => throw new InvalidOperationException($"Unsupported Provider message role '{role}'.")
    };

    private static Dictionary<string, object?> ConvertArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["value"] = arguments.Clone()
            };
        }

        var converted = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            converted[property.Name] = property.Value.Clone();
        }

        return converted;
    }

    private static object ConvertToolResult(ToolResultContentBlock toolResult)
    {
        if (toolResult.Content is [TextContentBlock text])
        {
            return text.Text;
        }

        return JsonSerializer.SerializeToElement(
            toolResult.Content,
            RuntimeJsonContext.Default.ContentBlockArray);
    }

    private static IEnumerable<RuntimeProviderEvent> MapUpdate(
        RuntimeProviderRequest request,
        AgentResponseUpdate update)
    {
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextReasoningContent reasoning
                    when !string.IsNullOrEmpty(reasoning.Text)
                         || !string.IsNullOrEmpty(reasoning.ProtectedData):
                    ProviderExtensionData[]? extensions = null;
                    if (!string.IsNullOrEmpty(reasoning.ProtectedData))
                    {
                        extensions =
                        [
                            ProviderExtensionData.CreateString(
                                ProviderExtensionPolicy.GetNamespace("openai"),
                                ProviderExtensionPolicy.ReasoningSignatureName,
                                reasoning.ProtectedData)
                        ];
                    }

                    yield return new ReasoningDeltaProviderEvent(
                        request.InvocationId,
                        reasoning.Text,
                        extensions);
                    break;

                case TextContent text when !string.IsNullOrEmpty(text.Text):
                    yield return new TextDeltaProviderEvent(request.InvocationId, text.Text);
                    break;

                case FunctionCallContent functionCall:
                    var argumentsJson = SerializeArguments(functionCall.Arguments);
                    var callId = functionCall.CallId ?? string.Empty;
                    var name = functionCall.Name ?? string.Empty;
                    var toolId = ResolveToolId(request.Tools, name);

                    yield return new ToolCallDeltaProviderEvent(
                        request.InvocationId,
                        callId,
                        name,
                        argumentsJson);
                    yield return new ToolCallCompleteProviderEvent(
                        request.InvocationId,
                        callId,
                        toolId,
                        name,
                        argumentsJson);
                    break;

                case UsageContent
                {
                    Details.InputTokenCount: { } inputTokens,
                    Details.OutputTokenCount: { } outputTokens
                }:
                    yield return new UsageUpdatedProviderEvent(
                        request.InvocationId,
                        ClampTokenCount(inputTokens),
                        ClampTokenCount(outputTokens));
                    break;
            }
        }
    }

    private static IEnumerable<ProjectionAdjustedProviderEvent> GetProjectionAdjustments(
        RuntimeProviderRequest request)
    {
        var supportedNamespace = ProviderExtensionPolicy.GetNamespace("openai");
        foreach (var reasoning in request.Messages
                     .SelectMany(static message => message.Content)
                     .OfType<ReasoningContentBlock>())
        {
            ProviderExtensionData.ValidateCollection(reasoning.ProviderExtensions);
            if (reasoning.ProviderExtensions is null)
            {
                continue;
            }

            foreach (var extension in reasoning.ProviderExtensions)
            {
                if (string.Equals(extension.Namespace, supportedNamespace, StringComparison.Ordinal)
                    && string.Equals(
                        extension.Name,
                        ProviderExtensionPolicy.ReasoningSignatureName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                yield return new ProjectionAdjustedProviderEvent(
                    request.InvocationId,
                    "provider_extension_omitted",
                    extension.Namespace,
                    extension.Name);
            }
        }
    }

    private static string ResolveToolId(ToolDescriptor[]? tools, string name)
    {
        if (tools is null)
        {
            return name;
        }

        foreach (var tool in tools)
        {
            if (string.Equals(tool.DisplayName, name, StringComparison.Ordinal))
            {
                return tool.ToolId;
            }
        }

        return name;
    }

    private static string SerializeArguments(IDictionary<string, object?>? arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            if (arguments is not null)
            {
                foreach (var argument in arguments)
                {
                    writer.WritePropertyName(argument.Key);
                    WriteJsonValue(writer, argument.Value);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            case JsonNode node:
                node.WriteTo(writer);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static int ClampTokenCount(long value)
    {
        return (int)Math.Clamp(value, 0, int.MaxValue);
    }

    private static RuntimeError CreateProviderError(Exception exception)
    {
        int? statusCode = exception switch
        {
            ClientResultException clientResult when clientResult.Status > 0 => clientResult.Status,
            HttpRequestException { StatusCode: { } httpStatus } => (int)httpStatus,
            _ => (int?)null
        };
        string? retryAfter = null;
        string? requestId = null;
        if (exception is ClientResultException clientResultException
            && clientResultException.GetRawResponse() is { } response)
        {
            response.Headers.TryGetValue("retry-after", out retryAfter);
            if (!response.Headers.TryGetValue("request-id", out requestId))
            {
                response.Headers.TryGetValue("x-request-id", out requestId);
            }
        }

        return ProviderErrorFactory.Create(
            "OpenAI",
            exception,
            statusCode,
            retryAfter,
            requestId,
            isProtocolError: exception is JsonException);
    }

    public void Dispose()
    {
        _ownedHttpClient?.Dispose();
    }

}
