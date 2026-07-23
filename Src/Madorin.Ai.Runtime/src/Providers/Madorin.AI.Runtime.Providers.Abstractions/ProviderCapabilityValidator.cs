using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Providers.Abstractions;

public static class ProviderCapabilityValidator
{
    public static Task ValidateAsync(
        ProviderCapabilities providerCapabilities,
        RuntimeProviderRequest request,
        IReadOnlyList<ProviderModel> models,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(models);
        ct.ThrowIfCancellationRequested();

        var capabilities = GetEffectiveCapabilities(
            providerCapabilities,
            request.ModelId,
            models);

        if (request.Tools is { Length: > 0 } && !capabilities.ToolCalling)
        {
            throw CreateCapabilityException("tool_calling");
        }

        if (request.StructuredOutput is not null && !capabilities.StructuredOutput)
        {
            throw CreateCapabilityException("structured_output");
        }

        return ValidateAsync(
            capabilities,
            [.. request.Messages.SelectMany(static message => message.Content)],
            ct);
    }

    public static Task ValidateAsync(
        ProviderCapabilities capabilities,
        ContentBlock[] input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();

        foreach (var block in input)
        {
            ct.ThrowIfCancellationRequested();

            var unsupportedCapability = block switch
            {
                ReasoningContentBlock when !capabilities.Reasoning => "reasoning",
                ToolCallContentBlock or ToolResultContentBlock when !capabilities.ToolCalling => "tool_calling",
                BlobRefContentBlock { Blob.ContentType: var contentType }
                    when contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                         && !capabilities.Vision => "vision",
                BlobRefContentBlock { Blob.ContentType: var contentType }
                    when contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                         && !capabilities.Audio => "audio",
                BlobRefContentBlock when !capabilities.Files => "files",
                _ => null
            };

            if (unsupportedCapability is not null)
            {
                throw CreateCapabilityException(unsupportedCapability);
            }
        }

        return Task.CompletedTask;
    }

    private static ProviderCapabilities GetEffectiveCapabilities(
        ProviderCapabilities providerCapabilities,
        string modelId,
        IReadOnlyList<ProviderModel> models)
    {
        var model = models.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, modelId, StringComparison.Ordinal)
            || string.Equals(candidate.ProviderModelId, modelId, StringComparison.Ordinal));
        var modelCapabilities = model?.Capabilities;
        if (modelCapabilities is null)
        {
            return providerCapabilities;
        }

        return new ProviderCapabilities(
            Streaming: providerCapabilities.Streaming && modelCapabilities.Streaming,
            ToolCalling: providerCapabilities.ToolCalling && modelCapabilities.ToolCalling,
            Vision: providerCapabilities.Vision && modelCapabilities.Vision,
            Audio: providerCapabilities.Audio && modelCapabilities.Audio,
            StructuredOutput: providerCapabilities.StructuredOutput && modelCapabilities.StructuredOutput,
            Reasoning: providerCapabilities.Reasoning && modelCapabilities.Reasoning,
            PromptCaching: providerCapabilities.PromptCaching && modelCapabilities.PromptCaching,
            Files: providerCapabilities.Files && modelCapabilities.Files,
            ComputerUse: providerCapabilities.ComputerUse && modelCapabilities.ComputerUse,
            Embeddings: providerCapabilities.Embeddings && modelCapabilities.Embeddings,
            Usage: providerCapabilities.Usage && modelCapabilities.Usage,
            RemoteCancellation: providerCapabilities.RemoteCancellation && modelCapabilities.RemoteCancellation);
    }

    private static RuntimeProviderException CreateCapabilityException(string capability)
    {
        var error = new RuntimeError(
            RuntimeErrorCodes.CapabilityNotSupported,
            "capability",
            $"Provider capability '{capability}' is not supported for this request.",
            IsRetryable: false,
            ProviderDetails: null,
            DiagnosticId: Guid.NewGuid().ToString("N"));

        return new RuntimeProviderException(error);
    }
}
