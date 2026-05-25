using DesktopPet.Behaviors;
using DesktopPet.Models.Live2D;
using System.Threading;

internal sealed class SimpleMouthMotionLoop : IDisposable
{
    private readonly Live2DModel _model;
    private readonly CancellationTokenSource _stopped = new();
    private Thread? _thread;
    private int _state = (int)PetState.Idle;
    private long _speakUntilTick;
    private bool _disposed;

    public SimpleMouthMotionLoop(Live2DModel model)
    {
        _model = model;
    }

    public PetState State => (PetState)Volatile.Read(ref _state);

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Animate)
        {
            IsBackground = true,
            Name = "DesktopPet Live2D Mouth Motion"
        };
        _thread.Start();
    }

    public void SetState(PetState state)
    {
        if (_disposed) return;
        Volatile.Write(ref _state, (int)state);
    }

    public void SpeakFor(TimeSpan duration)
    {
        if (_disposed) return;

        if (duration <= TimeSpan.Zero)
        {
            SetState(PetState.Idle);
            return;
        }

        Interlocked.Exchange(ref _speakUntilTick, Environment.TickCount64 + (long)duration.TotalMilliseconds);
        SetState(PetState.Speaking);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopped.Cancel();
        _thread?.Join();
        _thread = null;
        _model.SetMouthOpen(0.0f);
        _stopped.Dispose();
    }

    private void Animate()
    {
        var current = 0.0f;
        while (!_stopped.IsCancellationRequested)
        {
            var now = Environment.TickCount64;
            var state = State;
            if (state == PetState.Speaking && now >= Interlocked.Read(ref _speakUntilTick))
            {
                SetState(PetState.Idle);
                state = PetState.Idle;
            }

            var target = GetTargetMouthOpen(state, now);
            current = Approach(current, target, state is PetState.Idle or PetState.Hidden ? 0.18f : 0.35f);
            _model.SetMouthOpen(current);
            if (_stopped.Token.WaitHandle.WaitOne(33))
            {
                return;
            }
        }
    }

    private static float GetTargetMouthOpen(PetState state, long milliseconds)
    {
        return state switch
        {
            PetState.Speaking => 0.18f + (float)((Math.Sin(milliseconds / 95.0) + 1.0) * 0.38),
            PetState.Thinking => 0.04f + (float)((Math.Sin(milliseconds / 220.0) + 1.0) * 0.08),
            _ => 0.0f
        };
    }

    private static float Approach(float current, float target, float amount)
    {
        return current + (target - current) * Math.Clamp(amount, 0.0f, 1.0f);
    }
}
