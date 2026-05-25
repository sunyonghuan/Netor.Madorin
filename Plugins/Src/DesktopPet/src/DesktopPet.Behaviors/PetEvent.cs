namespace DesktopPet.Behaviors;

public sealed record PetEvent(
    PetEventKind Kind,
    string? Text = null,
    DateTimeOffset? OccurredAt = null);
