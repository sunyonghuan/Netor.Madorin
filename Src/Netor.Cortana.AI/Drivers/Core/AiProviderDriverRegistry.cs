using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI.Drivers;

/// <summary>
/// 厂商驱动注册表。统一解析驱动并向 UI 提供可选定义。
/// </summary>
public sealed class AiProviderDriverRegistry(IEnumerable<IAiProviderDriver> drivers)
{
    private readonly IAiProviderDriver[] _drivers = drivers.ToArray();

    public IReadOnlyList<AiProviderDriverDefinition> GetDefinitions() =>
        _drivers.Select(static driver => driver.Definition).ToArray();

    public bool IsRegistered(string? driverId) =>
        !string.IsNullOrWhiteSpace(driverId)
        && _drivers.Any(driver => string.Equals(driver.Definition.Id, driverId, StringComparison.OrdinalIgnoreCase));

    public IAiProviderDriver Resolve(AiProviderEntity provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var driver = _drivers.FirstOrDefault(candidate => candidate.CanHandle(provider));
        if (driver is null)
        {
            throw new InvalidOperationException($"未找到可处理厂商类型 '{provider.ProviderType}' 的 AI 驱动。");
        }

        return driver;
    }
}