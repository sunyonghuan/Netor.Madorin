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

    /// <summary>插件专属的数据存储目录。</summary>
    public string DataDirectory { get; }

    /// <summary>当前工作区目录。</summary>
    public string WorkspaceDirectory { get; }

    /// <summary>插件目录。</summary>
    public string PluginDirectory { get; }

    /// <summary>WebSocket 服务器端口。</summary>
    public int WsPort { get; }

    /// <summary>插件 init 扩展参数。</summary>
    public IReadOnlyDictionary<string, string> Extensions { get; }

    /// <summary>旧聊天 WebSocket 端点。</summary>
    public string ChatWsEndpoint { get; }

    /// <summary>内部对话 feed 端点。</summary>
    public string ConversationFeedEndpoint { get; }

    /// <summary>内部对话 feed 协议名。</summary>
    public string ConversationFeedProtocol { get; }

    /// <summary>内部对话 feed 协议版本。</summary>
    public string ConversationFeedVersion { get; }

    /// <summary>内部对话 feed 端口（扩展字段，优先于 Endpoint）。</summary>
    public int ConversationFeedPort { get; }

    /// <summary>直接构造运行时配置。</summary>
    /// <param name="dataDirectory">插件专属的数据存储目录。</param>
    /// <param name="workspaceDirectory">当前工作区目录。</param>
    /// <param name="pluginDirectory">插件目录。</param>
    /// <param name="wsPort">WebSocket 服务器端口。</param>
    /// <param name="chatWsEndpoint">旧聊天 WebSocket 端点。</param>
    /// <param name="conversationFeedEndpoint">内部对话 feed 端点。</param>
    /// <param name="conversationFeedProtocol">内部对话 feed 协议名。</param>
    /// <param name="conversationFeedVersion">内部对话 feed 协议版本。</param>
    /// <param name="conversationFeedPort">内部对话 feed 端口。</param>
    /// <param name="extensions">插件 init 扩展参数。</param>
    public PluginSettings(
        string dataDirectory,
        string workspaceDirectory,
        string pluginDirectory,
        int wsPort,
        string chatWsEndpoint,
        string conversationFeedEndpoint,
        string conversationFeedProtocol,
        string conversationFeedVersion,
        int conversationFeedPort = 0,
        IReadOnlyDictionary<string, string>? extensions = null)
    {
        DataDirectory = dataDirectory;
        WorkspaceDirectory = workspaceDirectory;
        PluginDirectory = pluginDirectory;
        WsPort = wsPort;
        Extensions = extensions is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(extensions, StringComparer.Ordinal);
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
        ConversationFeedPort = conversationFeedPort;
    }

    /// <summary>
    /// 从宿主传入的 JSON 解析配置。
    /// </summary>
    public static PluginSettings FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var dataDirectory = root.TryGetProperty("dataDirectory", out var dataDirectoryElement)
            ? dataDirectoryElement.GetString() ?? string.Empty
            : string.Empty;

        var workspaceDirectory = root.TryGetProperty("workspaceDirectory", out var workspaceDirectoryElement)
            ? workspaceDirectoryElement.GetString() ?? string.Empty
            : string.Empty;

        var pluginDirectory = root.TryGetProperty("pluginDirectory", out var pluginDirectoryElement)
            ? pluginDirectoryElement.GetString() ?? string.Empty
            : string.Empty;

        var wsPort = root.TryGetProperty("wsPort", out var wsPortElement)
            ? wsPortElement.GetInt32()
            : 0;

        var extensions = ReadExtensions(root);
        var chatWsEndpoint = GetExtension(extensions, "chatWsEndpoint");
        var conversationFeedEndpoint = GetExtension(extensions, "conversationFeedEndpoint");
        var conversationFeedProtocol = GetExtension(extensions, "conversationFeedProtocol");
        var conversationFeedVersion = GetExtension(extensions, "conversationFeedVersion");
        var conversationFeedPort = GetExtensionInt32(extensions, "conversationFeedPort");

        return new PluginSettings(
            dataDirectory,
            workspaceDirectory,
            pluginDirectory,
            wsPort,
            chatWsEndpoint,
            conversationFeedEndpoint,
            conversationFeedProtocol,
            conversationFeedVersion,
            conversationFeedPort,
            extensions);
    }

    private static Dictionary<string, string> ReadExtensions(JsonElement root)
    {
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("extensions", out var extensionsElement) || extensionsElement.ValueKind != JsonValueKind.Object)
            return extensions;

        foreach (var property in extensionsElement.EnumerateObject())
        {
            extensions[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
        }

        return extensions;
    }

    private static string GetExtension(IReadOnlyDictionary<string, string> extensions, string name)
    {
        return extensions.TryGetValue(name, out var value) ? value : string.Empty;
    }

    private static int GetExtensionInt32(IReadOnlyDictionary<string, string> extensions, string name)
    {
        return extensions.TryGetValue(name, out var value) && int.TryParse(value, out var number) ? number : 0;
    }
}