namespace Netor.Cortana.Entitys;

/// <summary>
/// Cortana 宿主模型能力控制面常量。
/// </summary>
public static class ModelCapabilityProtocol
{
    public const string Path = CortanaWsEndpoints.PluginBusPath;
    public const string Protocol = CortanaWsEndpoints.PluginBusProtocol;
    public const string Version = CortanaWsEndpoints.PluginBusVersion;
    public const string LlmInvokeOperation = CortanaWsEndpoints.ModelCapabilityRequestOperation;

    public static string BuildEndpoint(int port) => CortanaWsEndpoints.BuildPluginBusEndpoint(port);
}
