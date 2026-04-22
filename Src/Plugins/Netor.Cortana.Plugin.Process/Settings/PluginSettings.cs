using System.Text.Json;

namespace Netor.Cortana.Plugin;

/// <summary>
/// 插件运行时配置，由宿主在 init 阶段注入。
/// </summary>
public sealed class PluginSettings
{
	/// <summary>
	/// 插件专属的数据存储目录。
	/// </summary>
	public string DataDirectory { get; }

	/// <summary>
	/// 当前工作区目录。
	/// </summary>
	public string WorkspaceDirectory { get; }

	/// <summary>
	/// 插件目录。
	/// </summary>
	public string PluginDirectory { get; }

	/// <summary>
	/// WebSocket 服务器端口。
	/// </summary>
	public int WsPort { get; }

	/// <summary>
	/// 直接构造运行时配置。
	/// </summary>
	public PluginSettings(
		string dataDirectory,
		string workspaceDirectory,
		string pluginDirectory,
		int wsPort)
	{
		DataDirectory = dataDirectory;
		WorkspaceDirectory = workspaceDirectory;
		PluginDirectory = pluginDirectory;
		WsPort = wsPort;
	}

	/// <summary>
	/// 从宿主传入的 JSON 解析配置。
	/// </summary>
	public static PluginSettings FromJson(string json)
	{
		ArgumentNullException.ThrowIfNull(json);

		using var doc = JsonDocument.Parse(json);
		var root = doc.RootElement;

		var dataDirectory = root.TryGetProperty("dataDirectory", out var dd)
			? dd.GetString() ?? string.Empty
			: string.Empty;

		var workspaceDirectory = root.TryGetProperty("workspaceDirectory", out var wd)
			? wd.GetString() ?? string.Empty
			: string.Empty;

		var pluginDirectory = root.TryGetProperty("pluginDirectory", out var pd)
			? pd.GetString() ?? string.Empty
			: string.Empty;

		int wsPort = root.TryGetProperty("wsPort", out var wp)
			? wp.GetInt32()
			: 0;

		return new PluginSettings(dataDirectory, workspaceDirectory, pluginDirectory, wsPort);
	}
}