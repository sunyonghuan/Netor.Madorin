namespace DesktopPet.Rendering.D3D11;

public interface IRenderHost : IDisposable
{
    RenderFrameStats FrameStats { get; }

    /// <summary>Global model scale multiplier driven by the mouse wheel (default 1.0).</summary>
    float ModelScale { get; set; }

    /// <summary>
    /// Extra rotation applied to all 3D mesh items on top of their own animation.
    /// Yaw = Y-axis rotation (horizontal drag), Pitch = X-axis rotation (vertical drag).
    /// Driven by right-mouse-button drag. Unit: radians.
    /// </summary>
    (float Yaw, float Pitch) MeshExtraRotation { get; set; }

    /// <summary>Sets the subtitle text shown at the bottom of the window. Pass null or empty to hide.</summary>
    void SetSubtitle(string? text);

    /// <summary>
    /// When true, draws a semi-transparent border around the window edge.
    /// Toggled by mouse enter/leave so the user can see the window boundary.
    /// </summary>
    bool ShowBorder { get; set; }

    void Start();

    void Stop();

    void Resize(int width, int height);

    void SubmitRenderItems(IReadOnlyList<D3D11RenderItem> renderItems);

    void SubmitMeshItems(IReadOnlyList<D3D11MeshItem> meshItems);
}
