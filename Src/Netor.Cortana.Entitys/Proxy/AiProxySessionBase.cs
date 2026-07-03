namespace Netor.Cortana.Entitys.Proxy;

/// <summary>
/// Proxy 专用会话基类。
/// 用于隔离外部客户端连续对话，不应写入主聊天历史。
/// </summary>
public abstract class AiProxySessionBase : IAiProxySession
{
    private readonly object _gate = new();
    private readonly List<AiProxyMessage> _messages = [];

    protected AiProxySessionBase(string sessionKey)
    {
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            throw new ArgumentException("Proxy session key cannot be empty.", nameof(sessionKey));
        }

        SessionKey = sessionKey;
        CreatedAt = DateTimeOffset.Now;
        LastAccessAt = CreatedAt;
    }

    public string SessionKey { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LastAccessAt { get; private set; }

    public IReadOnlyList<AiProxyMessage> Messages
    {
        get
        {
            lock (_gate)
            {
                TouchCore();
                return _messages.ToArray();
            }
        }
    }

    public void Append(AiProxyMessage message)
    {
        lock (_gate)
        {
            _messages.Add(message);
            TouchCore();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _messages.Clear();
            TouchCore();
        }
    }

    protected void Touch()
    {
        lock (_gate)
        {
            TouchCore();
        }
    }

    private void TouchCore()
    {
        LastAccessAt = DateTimeOffset.Now;
    }
}
