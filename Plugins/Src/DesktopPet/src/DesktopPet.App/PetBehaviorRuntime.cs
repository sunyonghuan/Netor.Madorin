using DesktopPet.Ai;
using DesktopPet.Behaviors;

internal sealed class PetBehaviorRuntime : IDisposable
{
    private readonly PetBehaviorStateMachine _stateMachine;
    private SimpleMouthMotionLoop? _mouthMotionLoop;
    private readonly Action<Exception, string>? _logError;
    private readonly CancellationTokenSource _stopped = new();
    private Task? _webSocketTask;
    private bool _disposed;

    public PetBehaviorRuntime(
        PetBehaviorStateMachine stateMachine,
        SimpleMouthMotionLoop? mouthMotionLoop,
        Action<Exception, string>? logError = null)
    {
        _stateMachine = stateMachine;
        _mouthMotionLoop = mouthMotionLoop;
        _logError = logError;
    }

    public PetBehaviorSnapshot Current => _stateMachine.Current;

    /// <summary>Replaces the mouth animation target after a model switch.</summary>
    public void SetMouthMotionLoop(SimpleMouthMotionLoop? loop)
    {
        _mouthMotionLoop = loop;
        if (loop is not null)
        {
            loop.SetState(_stateMachine.Current.State);
        }
    }

    public PetBehaviorSnapshot Apply(PetEvent petEvent)
    {
        var snapshot = _stateMachine.Apply(petEvent);
        _mouthMotionLoop?.SetState(snapshot.State);
        return snapshot;
    }

    public void StartWebSocket(Uri uri)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_webSocketTask is not null)
        {
            return;
        }

        _webSocketTask = Task.Run(() => RunWebSocketAsync(uri, _stopped.Token));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopped.Cancel();
        try
        {
            _webSocketTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(exception => exception is OperationCanceledException))
        {
        }

        _stopped.Dispose();
    }

    private async Task RunWebSocketAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            await using var client = new CortanaRealtimeClient(uri);
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var petEvent in client.ReadPetEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                Apply(petEvent);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logError?.Invoke(ex, "Cortana WebSocket stream stopped");
            Console.Error.WriteLine($"Cortana WebSocket stream stopped: {ex.Message}");
            Apply(new PetEvent(PetEventKind.Idle));
        }
    }
}
