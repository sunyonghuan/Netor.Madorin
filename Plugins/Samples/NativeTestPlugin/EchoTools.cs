using Netor.Cortana.Plugin;

namespace NativeTestPlugin;

/// <summary>
/// 回显工具，用于测试原生通道的基本通信功能。
/// </summary>
[Tool]
public class EchoTools
{
    private readonly PluginSettings _settings;

    public EchoTools(PluginSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// 回显传入的消息，附带数据目录信息。
    /// </summary>
    [Tool(Name = "echo_message", Description = "回显传入的消息，用于测试原生通道的基本通信功能。")]
    public string EchoMessage(
        [Parameter(Description = "要回显的消息内容")] string message)
    {
        return $"[回显] {message}（数据目录: {_settings.DataDirectory}）";
    }
}