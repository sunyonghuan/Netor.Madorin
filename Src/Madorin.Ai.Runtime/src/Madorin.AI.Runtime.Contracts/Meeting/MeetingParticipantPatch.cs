using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Contracts;

/// <summary>
/// A single patch operation on the meeting participant list.
/// Exactly one property must be non-null.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(AddParticipantPatch), "add")]
[JsonDerivedType(typeof(RemoveParticipantPatch), "remove")]
[JsonDerivedType(typeof(UpdateParticipantPatch), "update")]
[JsonDerivedType(typeof(UpdateParticipantStatusPatch), "updateStatus")]
[JsonDerivedType(typeof(ReorderParticipantsPatch), "reorder")]
public abstract record MeetingParticipantPatch;

/// <summary>Adds a new participant to the meeting.</summary>
public sealed record AddParticipantPatch(MeetingParticipant Participant) : MeetingParticipantPatch;

/// <summary>Marks a participant as Removed without deleting history.</summary>
public sealed record RemoveParticipantPatch(string ParticipantId) : MeetingParticipantPatch;

/// <summary>Replaces the full definition of an existing participant.</summary>
public sealed record UpdateParticipantPatch(MeetingParticipant Participant) : MeetingParticipantPatch;

/// <summary>Changes only the status of an existing participant.</summary>
public sealed record UpdateParticipantStatusPatch(
    string ParticipantId,
    ParticipantStatus Status) : MeetingParticipantPatch;

/// <summary>Explicitly sets the join order for the listed participants.</summary>
public sealed record ReorderParticipantsPatch(ParticipantJoinOrder[] Orders) : MeetingParticipantPatch;

/// <summary>Maps a participant to a new join order position.</summary>
public sealed record ParticipantJoinOrder(string ParticipantId, int JoinOrder);
