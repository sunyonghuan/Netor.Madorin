using System.Text;

namespace DesktopPet.Behaviors;

public sealed class PetBehaviorStateMachine
{
    private readonly object _syncRoot = new();
    private readonly StringBuilder _subtitle = new();
    private PetState _state = PetState.Idle;
    private DateTimeOffset _updatedAt = DateTimeOffset.UtcNow;

    public PetBehaviorSnapshot Current
    {
        get
        {
            lock (_syncRoot)
            {
                return CreateSnapshot();
            }
        }
    }

    public PetBehaviorSnapshot Apply(PetEvent petEvent)
    {
        ArgumentNullException.ThrowIfNull(petEvent);

        lock (_syncRoot)
        {
            _updatedAt = petEvent.OccurredAt ?? DateTimeOffset.UtcNow;
            ApplyCore(petEvent);
            return CreateSnapshot();
        }
    }

    private void ApplyCore(PetEvent petEvent)
    {
        switch (petEvent.Kind)
        {
            case PetEventKind.Show:
                if (_state == PetState.Hidden)
                {
                    _state = PetState.Idle;
                }

                break;

            case PetEventKind.Hide:
                _state = PetState.Hidden;
                _subtitle.Clear();
                break;

            case PetEventKind.Idle:
                if (_state != PetState.Hidden)
                {
                    _state = PetState.Idle;
                }

                break;

            case PetEventKind.Think:
                if (_state != PetState.Hidden)
                {
                    _state = PetState.Thinking;
                    ReplaceSubtitle(petEvent.Text);
                }

                break;

            case PetEventKind.Speak:
                if (_state != PetState.Hidden)
                {
                    _state = PetState.Speaking;
                    ReplaceSubtitle(petEvent.Text);
                }

                break;

            case PetEventKind.TextDelta:
                if (_state != PetState.Hidden)
                {
                    _state = PetState.Speaking;
                    _subtitle.Append(petEvent.Text);
                }

                break;

            case PetEventKind.ClearText:
                _subtitle.Clear();
                if (_state != PetState.Hidden)
                {
                    _state = PetState.Idle;
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(petEvent), petEvent.Kind, "Unsupported pet event kind.");
        }
    }

    private void ReplaceSubtitle(string? text)
    {
        _subtitle.Clear();
        if (!string.IsNullOrEmpty(text))
        {
            _subtitle.Append(text);
        }
    }

    private PetBehaviorSnapshot CreateSnapshot()
    {
        return new PetBehaviorSnapshot(
            _state,
            _subtitle.ToString(),
            _state != PetState.Hidden,
            _state == PetState.Speaking,
            _state == PetState.Thinking,
            _updatedAt);
    }
}
