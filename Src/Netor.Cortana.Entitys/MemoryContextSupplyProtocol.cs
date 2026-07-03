namespace Netor.Cortana.Entitys;

/// <summary>
/// 长期记忆上下文供应控制面协议常量。
/// </summary>
public static class MemoryContextSupplyProtocol
{
    public const string Protocol = CortanaWsEndpoints.PluginBusProtocol;
    public const string Version = CortanaWsEndpoints.PluginBusVersion;
    public const string SupplyRequestOperation = CortanaWsEndpoints.MemoryContextSupplyRequestOperation;
    public const string SupplyPackageOperation = CortanaWsEndpoints.MemoryContextSupplyResponseOperation;
    public const string SupplyErrorOperation = CortanaWsEndpoints.MemoryContextSupplyErrorOperation;
}
