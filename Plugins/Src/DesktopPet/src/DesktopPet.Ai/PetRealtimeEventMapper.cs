using DesktopPet.Behaviors;

namespace DesktopPet.Ai;

public sealed class PetRealtimeEventMapper
{
    public PetEvent? Map(CortanaWsServerMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message.Type switch
        {
            "token" => new PetEvent(PetEventKind.TextDelta, message.Data),
            "done" => new PetEvent(PetEventKind.Idle),
            "tts_started" => new PetEvent(PetEventKind.Speak),
            "tts_subtitle" => new PetEvent(PetEventKind.Speak, message.Data),
            "tts_completed" => new PetEvent(PetEventKind.Idle),
            "chat_completed" => new PetEvent(PetEventKind.ClearText),
            "stt_partial" => new PetEvent(PetEventKind.Think, message.Data),
            "stt_final" => new PetEvent(PetEventKind.Think, message.Data),
            "wakeword_detected" => new PetEvent(PetEventKind.Think, "唤醒中"),
            "error" => new PetEvent(PetEventKind.Idle, message.Data),
            _ => null
        };
    }
}
