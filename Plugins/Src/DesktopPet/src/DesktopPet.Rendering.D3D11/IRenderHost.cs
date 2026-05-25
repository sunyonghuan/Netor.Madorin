namespace DesktopPet.Rendering.D3D11;

public interface IRenderHost : IDisposable
{
    RenderFrameStats FrameStats { get; }

    /// <summary>Global model scale multiplier driven by the mouse wheel (default 1.0).</summary>
    float ModelScale { get; set; }

    /// <summary>Sets the subtitle text shown at the bottom of the window. Pass null or empty to hide.</summary>
    void SetSubtitle(string? text);

    void Start();

    void Stop();

    void Resize(int width, int height);

    void SubmitRenderItems(IReadOnlyList<D3D11RenderItem> renderItems);

    void SubmitMeshItems(IReadOnlyList<D3D11MeshItem> meshItems);
}
