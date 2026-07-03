namespace Netor.Cortana.AI.Drivers;

/// <summary>
/// AI 厂商驱动的可展示定义。
/// UI 只依赖此定义构建选择器，不感知具体驱动实现。
/// </summary>
public sealed record AiProviderDriverDefinition(
    string Id,
    string DisplayName,
    bool SupportsModelDiscovery);