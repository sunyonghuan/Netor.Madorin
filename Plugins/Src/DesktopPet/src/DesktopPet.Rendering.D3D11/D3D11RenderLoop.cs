namespace DesktopPet.Rendering.D3D11;

public sealed class D3D11RenderLoop : IRenderLoop, IDisposable
{
    private readonly Action _renderFrame;
    private readonly CancellationTokenSource _stopped = new();
    private Thread? _thread;
    private bool _disposed;

    public D3D11RenderLoop(Action renderFrame)
    {
        _renderFrame = renderFrame ?? throw new ArgumentNullException(nameof(renderFrame));
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Render)
        {
            IsBackground = true,
            Name = "DesktopPet D3D11 Render Loop"
        };
        _thread.Start();
    }

    public void Stop()
    {
        if (_thread is null)
        {
            return;
        }

        _stopped.Cancel();
        _thread.Join();
        _thread = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _stopped.Dispose();
    }

    private void Render()
    {
        while (!_stopped.IsCancellationRequested)
        {
            _renderFrame();
        }
    }
}
