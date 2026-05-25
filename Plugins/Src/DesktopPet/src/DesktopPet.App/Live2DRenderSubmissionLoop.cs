using DesktopPet.Models.Live2D;
using DesktopPet.Rendering.D3D11;

internal sealed class Live2DRenderSubmissionLoop : IDisposable
{
    private readonly Live2DModel _model;
    private readonly IRenderHost _renderHost;
    private readonly CancellationTokenSource _stopped = new();
    private readonly bool _enableMotion;
    private Thread? _thread;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private bool _disposed;

    public Live2DRenderSubmissionLoop(Live2DModel model, IRenderHost renderHost, bool enableMotion = true)
    {
        _model = model;
        _renderHost = renderHost;
        _enableMotion = enableMotion;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(SubmitFrames)
        {
            IsBackground = true,
            Name = "DesktopPet Live2D Render Submission"
        };
        _thread.Start();
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
        _stopped.Dispose();
    }

    private void SubmitFrames()
    {
        while (!_stopped.IsCancellationRequested)
        {
            var elapsedSeconds = (float)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds;
            if (_enableMotion)
            {
                _model.AdvanceMotion(elapsedSeconds);
            }
            var snapshots = _model.ReadDrawableSnapshots();
            _renderHost.SubmitRenderItems(Live2DRenderItemMapper.ToRenderItems(snapshots, _model.Info.TexturePaths));
            if (_stopped.Token.WaitHandle.WaitOne(33))
            {
                return;
            }
        }
    }
}
