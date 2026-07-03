namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// Agent manifest 校验结果。
/// </summary>
public sealed record AgentManifestValidationResult(bool IsValid, string? Error)
{
    public static AgentManifestValidationResult Success { get; } = new(true, null);

    public static AgentManifestValidationResult Fail(string error) => new(false, error);
}
