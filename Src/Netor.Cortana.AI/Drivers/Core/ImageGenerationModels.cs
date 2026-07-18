using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI.Drivers;

public sealed record ProviderImageGenerationRequest(
    AiProviderEntity Provider,
    AiModelEntity Model,
    string Prompt);

public sealed record ImageGenerationResult(
    string FileName,
    string MimeType,
    byte[] Data);

public sealed record ProviderVideoGenerationRequest(
    AiProviderEntity Provider,
    AiModelEntity Model,
    string Prompt);

public sealed record VideoGenerationResult(
    string FileName,
    string MimeType,
    byte[] Data);
