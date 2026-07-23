using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Services;

/// <summary>Describes the latest persisted state required to resume a Session.</summary>
public sealed record SessionResumeInfo(
    string SessionId,
    RuntimeMode Mode,
    string LastRunId,
    string LastStatus);
