namespace Netor.Madorin.Plugin;

/// <summary>
/// 支持宿主侧直接调用工具的插件。
/// </summary>
public interface IInvokablePlugin : IPlugin
{
    /// <summary>
    /// 调用指定工具。<paramref name="toolName"/> 可以是完整工具名，也可以是插件元数据中的 shortName。
    /// </summary>
    Task<string> InvokeToolAsync(string toolName, string argsJson, CancellationToken cancellationToken = default);
}
