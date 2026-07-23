namespace Madorin.AI.Runtime.Client;

public enum RuntimeClientState
{
    Starting,
    Connecting,
    Authenticating,
    Initializing,
    Connected,
    Reconnecting,
    Closed,
    Faulted
}
