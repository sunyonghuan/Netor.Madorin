using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Tools.Abstractions;

/// <summary>Owns immutable merged catalog snapshots for model invocations.</summary>
public interface IToolCatalogStore
{
    /// <summary>Returns the current immutable catalog snapshot.</summary>
    public ToolCatalogSnapshot CaptureSnapshot();

    /// <summary>Atomically replaces all host-owned tools.</summary>
    public ToolCatalogSnapshot ReplaceHostCatalog(ToolCatalogReplaceRequest request);

    /// <summary>Applies a version-checked patch to host-owned tools.</summary>
    public ToolCatalogSnapshot PatchHostCatalog(ToolCatalogPatchRequest request);
}
