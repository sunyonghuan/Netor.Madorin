namespace Madorin.AI.Runtime.Contracts;

public sealed record SessionMessagesListParameters(
    string SessionId,
    long? Cursor = null,
    int PageSize = 100);
