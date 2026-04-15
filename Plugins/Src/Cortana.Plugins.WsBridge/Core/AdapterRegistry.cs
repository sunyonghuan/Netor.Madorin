namespace Cortana.Plugins.WsBridge.Core;

/// <summary>
/// 适配器注册表，编译期注册所有可用的外部应用适配器。
/// </summary>
public sealed class AdapterRegistry
{
    private readonly Dictionary<string, IExternalAppAdapter> _adapters = new(StringComparer.OrdinalIgnoreCase);

    public AdapterRegistry()
    {
        // 编译期注册，避免运行时反射扫描，保持 AOT 兼容。
        Register(new Adapters.GenericAdapter());
    }

    public void Register(IExternalAppAdapter adapter) => _adapters[adapter.AdapterId] = adapter;

    public IExternalAppAdapter? Get(string adapterId) => _adapters.GetValueOrDefault(adapterId);

    public IReadOnlyCollection<string> GetAdapterIds() => _adapters.Keys;
}
