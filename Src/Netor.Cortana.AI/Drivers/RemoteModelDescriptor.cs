namespace Netor.Cortana.AI.Drivers;

/// <summary>
/// 远程厂商返回的模型描述。
/// 由具体驱动负责转换为统一结构。
/// </summary>
public sealed record RemoteModelDescriptor(
    string Name,
    string? DisplayName,
    string? Description,
    string ModelType,
    int? ContextLength);