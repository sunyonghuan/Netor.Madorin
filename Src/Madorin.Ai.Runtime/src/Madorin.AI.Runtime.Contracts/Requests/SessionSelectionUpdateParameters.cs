namespace Madorin.AI.Runtime.Contracts;

/// <summary>
/// Unified selection/patch update for a session.
/// Exactly one of <see cref="Selection"/> or <see cref="MeetingPatches"/> must be supplied;
/// the server validates the choice.
/// </summary>
public sealed record SessionSelectionUpdateParameters(
    string SessionId,
    int ExpectedSelectionVersion,
    NextTurnSelection? Selection = null,
    MeetingParticipantPatch[]? MeetingPatches = null);
