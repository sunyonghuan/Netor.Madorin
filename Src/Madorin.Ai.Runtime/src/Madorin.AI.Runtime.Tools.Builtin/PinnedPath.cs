using Microsoft.Win32.SafeHandles;

namespace Madorin.AI.Runtime.Tools.Builtin;

/// <summary>Holds an existing filesystem object so its path cannot be replaced during a tool call.</summary>
internal sealed class PinnedPath(
    string path,
    string pinnedTargetPath,
    SafeFileHandle handle) : IDisposable
{
    public SafeFileHandle Handle { get; } = handle;

    public string Path { get; } = path;

    public string PinnedTargetPath { get; } = pinnedTargetPath;

    public void Dispose() => Handle.Dispose();
}
