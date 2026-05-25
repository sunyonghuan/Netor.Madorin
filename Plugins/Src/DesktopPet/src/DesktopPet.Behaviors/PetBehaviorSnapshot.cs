namespace DesktopPet.Behaviors;

public sealed record PetBehaviorSnapshot(
    PetState State,
    string Subtitle,
    bool IsVisible,
    bool IsSpeaking,
    bool IsThinking,
    DateTimeOffset UpdatedAt);
