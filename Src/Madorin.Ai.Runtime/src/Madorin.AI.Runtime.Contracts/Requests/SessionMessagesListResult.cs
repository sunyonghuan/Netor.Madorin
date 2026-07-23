namespace Madorin.AI.Runtime.Contracts;

public sealed record SessionMessagesListResult(
    SessionMessageDescriptor[] Messages,
    long? NextCursor);
