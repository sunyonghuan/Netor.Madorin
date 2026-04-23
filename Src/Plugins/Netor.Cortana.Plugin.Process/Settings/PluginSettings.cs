using System.Text.Json;

namespace Netor.Cortana.Plugin;

/// <summary>
/// 插件运行时配置，由宿主在 init 阶段注入。
/// </summary>
public sealed class PluginSettings
{
	private const string DefaultChatPath = "/ws/";
	private const string DefaultConversationFeedPath = "/internal/conversation-feed/";
	private const string DefaultConversationFeedProtocol = "conversation-feed";
	private const string DefaultConversationFeedVersion = "1.0.0";

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
	/// 旧聊天 WebSocket 端点。
	/// </summary>
	public string ChatWsEndpoint { get; }

	/// <summary>
	/// 内部对话 feed 端点。
	/// </summary>
	public string ConversationFeedEndpoint { get; }

	/// <summary>
	/// 内部对话 feed 协议名。
	/// </summary>
	public string ConversationFeedProtocol { get; }

	/// <summary>
	/// 内部对话 feed 协议版本。
	/// </summary>
	public string ConversationFeedVersion { get; }

	/// <summary>
	/// 直接构造运行时配置。
	/// </summary>
	public PluginSettings(
		string dataDirectory,
		string workspaceDirectory,
		string pluginDirectory,
		int wsPort,
		string chatWsEndpoint,
		string conversationFeedEndpoint,
		string conversationFeedProtocol,
		string conversationFeedVersion)
	{
		DataDirectory = dataDirectory;
		WorkspaceDirectory = workspaceDirectory;
		PluginDirectory = pluginDirectory;
		WsPort = wsPort;
		ChatWsEndpoint = !string.IsNullOrWhiteSpace(chatWsEndpoint)
			? chatWsEndpoint
			: wsPort > 0 ? $"ws://localhost:{wsPort}{DefaultChatPath}" : string.Empty;
		ConversationFeedEndpoint = !string.IsNullOrWhiteSpace(conversationFeedEndpoint)
			? conversationFeedEndpoint
			: wsPort > 0 ? $"ws://localhost:{wsPort}{DefaultConversationFeedPath}" : string.Empty;
		ConversationFeedProtocol = !string.IsNullOrWhiteSpace(conversationFeedProtocol)
			? conversationFeedProtocol
			: DefaultConversationFeedProtocol;
		ConversationFeedVersion = !string.IsNullOrWhiteSpace(conversationFeedVersion)
			? conversationFeedVersion
			: DefaultConversationFeedVersion;
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

		var chatWsEndpoint = string.Empty;
		var conversationFeedEndpoint = string.Empty;
		var conversationFeedProtocol = string.Empty;
		var conversationFeedVersion = string.Empty;

		if (root.TryGetProperty("extensions", out var extensionsElement) && extensionsElement.ValueKind == JsonValueKind.Object)
		{
			chatWsEndpoint = extensionsElement.TryGetProperty("chatWsEndpoint", out var chatWsEndpointElement)
				? chatWsEndpointElement.GetString() ?? string.Empty
				: string.Empty;

			conversationFeedEndpoint = extensionsElement.TryGetProperty("conversationFeedEndpoint", out var conversationFeedEndpointElement)
				? conversationFeedEndpointElement.GetString() ?? string.Empty
				: string.Empty;

			conversationFeedProtocol = extensionsElement.TryGetProperty("conversationFeedProtocol", out var conversationFeedProtocolElement)
				? conversationFeedProtocolElement.GetString() ?? string.Empty
				: string.Empty;

			conversationFeedVersion = extensionsElement.TryGetProperty("conversationFeedVersion", out var conversationFeedVersionElement)
				? conversationFeedVersionElement.GetString() ?? string.Empty
				: string.Empty;
		}

		return new PluginSettings(
			dataDirectory,
			workspaceDirectory,
			pluginDirectory,
			wsPort,
			chatWsEndpoint,
			conversationFeedEndpoint,
			conversationFeedProtocol,
			conversationFeedVersion);
	}
}