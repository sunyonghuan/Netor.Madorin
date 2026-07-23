namespace Madorin.AI.Runtime.Modes.Meeting;

/// <summary>Tracks the current participant and status of a meeting round.</summary>
public sealed record MeetingRoundState(
    int RoundIndex,
    string CurrentParticipantId,
    MeetingRoundStatus Status);

/// <summary>Describes the execution status of a meeting round.</summary>
public enum MeetingRoundStatus
{
    Pending,
    Running,
    Completed,
    Failed
}
