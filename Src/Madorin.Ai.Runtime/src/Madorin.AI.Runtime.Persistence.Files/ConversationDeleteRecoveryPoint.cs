namespace Madorin.AI.Runtime.Persistence.Files;

/// <summary>Describes files moved out of the live store before a Session deletion commits.</summary>
public sealed class ConversationDeleteRecoveryPoint
{
    internal ConversationDeleteRecoveryPoint(
        string sessionId,
        string recoveryDirectory,
        IReadOnlyList<Artifact> artifacts)
    {
        SessionId = sessionId;
        RecoveryDirectory = recoveryDirectory;
        Artifacts = artifacts;
    }

    public string SessionId { get; }

    public string RecoveryDirectory { get; }

    public string ManifestPath => Path.Combine(RecoveryDirectory, "manifest.json");

    public int QuarantinedBlobCount =>
        Artifacts.Count(static artifact => artifact.Kind == "blob");

    internal IReadOnlyList<Artifact> Artifacts { get; }

    internal sealed record Artifact(
        string Kind,
        string RelativePath,
        string LivePath,
        string RecoveryPath);
}
