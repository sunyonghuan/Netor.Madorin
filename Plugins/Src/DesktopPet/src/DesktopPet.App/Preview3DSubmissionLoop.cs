using DesktopPet.Models.Gltf;
using DesktopPet.Rendering.D3D11;

/// <summary>
/// Activated by --preview-3d flag. Loads Fox.glb (or DamagedHelmet.glb as fallback)
/// and submits it to the render host so the lighting / shader can be tested without
/// a full tray-menu model switch.
/// </summary>
internal sealed class Preview3DSubmissionLoop : IDisposable
{
    private readonly IRenderHost _renderHost;
    private readonly CancellationTokenSource _stopped = new();
    private GltfMeshSubmissionLoop? _inner;
    private Thread? _thread;
    private bool _disposed;

    public Preview3DSubmissionLoop(IRenderHost renderHost)
    {
        _renderHost = renderHost;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null) return;

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "DesktopPet 3D Preview"
        };
        _thread.Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopped.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(3));
        _inner?.Dispose();
        _stopped.Dispose();
    }

    private void Run()
    {
        var modelsDir = Path.Combine(AppContext.BaseDirectory, "assets", "gltf", "models");

        // Prefer Fox (colourful, has normals + texture), fall back to DamagedHelmet
        var candidates = new[] { "Fox", "DamagedHelmet" };
        GltfModel? model = null;
        string? loaded = null;

        foreach (var name in candidates)
        {
            var glbPath = Path.Combine(modelsDir, name, $"{name}.glb");
            if (!File.Exists(glbPath)) continue;
            try
            {
                model = new GltfModelLoader().Load(glbPath);
                loaded = name;
                break;
            }
            catch
            {
                // try next
            }
        }

        if (model is null)
        {
            Console.Error.WriteLine("[Preview3D] No GLB model found under " + modelsDir);
            return;
        }

        Console.WriteLine($"[Preview3D] Loaded '{loaded}': meshes={model.Summary.MeshCount}, " +
                          $"materials={model.Summary.MaterialCount}, animations={model.Summary.AnimationCount}");

        _inner = new GltfMeshSubmissionLoop(model, _renderHost);
        _inner.Start();

        // Keep alive until cancelled
        _stopped.Token.WaitHandle.WaitOne();
    }
}
