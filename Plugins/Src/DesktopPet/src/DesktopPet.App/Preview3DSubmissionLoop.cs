using System.Numerics;
using DesktopPet.Rendering.D3D11;

internal sealed class Preview3DSubmissionLoop : IDisposable
{
    private static readonly D3D11MeshVertex[] Vertices =
    [
        new(-0.65f, -0.55f, -0.35f, 0.95f, 0.25f, 0.22f, 0.95f),
        new(0.65f, -0.55f, -0.35f, 0.20f, 0.75f, 0.95f, 0.95f),
        new(0.0f, 0.70f, -0.35f, 0.95f, 0.85f, 0.22f, 0.95f),
        new(0.0f, 0.0f, 0.65f, 0.60f, 0.35f, 0.95f, 0.95f)
    ];

    private static readonly ushort[] Indices =
    [
        0, 1, 2,
        0, 3, 1,
        1, 3, 2,
        2, 3, 0
    ];

    private readonly IRenderHost _renderHost;
    private readonly CancellationTokenSource _stopped = new();
    private Thread? _thread;
    private bool _disposed;

    public Preview3DSubmissionLoop(IRenderHost renderHost)
    {
        _renderHost = renderHost;
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
            Name = "DesktopPet 3D Preview Submission"
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
        _thread?.Join(TimeSpan.FromSeconds(2));
        _renderHost.SubmitMeshItems([]);
        _stopped.Dispose();
    }

    private void SubmitFrames()
    {
        while (!_stopped.IsCancellationRequested)
        {
            var rotation = (float)(Environment.TickCount64 / 1000.0);
            var world = Matrix4x4.CreateScale(0.95f) * Matrix4x4.CreateRotationY(rotation);
            _renderHost.SubmitMeshItems(
            [
                new D3D11MeshItem("PreviewPyramid", Vertices, Indices, world)
            ]);
            Thread.Sleep(33);
        }
    }
}
