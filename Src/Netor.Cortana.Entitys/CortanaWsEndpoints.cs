namespace Netor.Cortana.Entitys;

/// <summary>
/// Cortana 宿主对外和对内 WebSocket 端点常量。
/// </summary>
public static class CortanaWsEndpoints
{
    public const string ChatPath = "/ws/";
    public const string ConversationFeedPath = "/internal/conversation-feed/";
    public const string ConversationFeedProtocol = "conversation-feed";
    public const string ConversationFeedVersion = "1.0.0";
    public const string ConversationTopic = "conversation";

    public static string BuildChatEndpoint(int port) => $"ws://localhost:{port}{ChatPath}";

    public static string BuildConversationFeedEndpoint(int port) =>
        $"ws://localhost:{port}{ConversationFeedPath}";
}