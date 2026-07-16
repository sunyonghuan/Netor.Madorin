namespace Netor.Cortana.UI.Controls.Shared.Internal;

/// <summary>
/// <see cref="WatcherEventBatcher"/> drain 后输出的动作记录。
/// </summary>
internal enum WatcherEventKind
{
    Created,
    Deleted,
    Renamed,
    Overflow,
}

/// <summary>
/// batcher drain 出的一条最终动作。<see cref="OldPath"/> 只在 <see cref="WatcherEventKind.Renamed"/>
/// 时有值；<see cref="WatcherEventKind.Overflow"/> 时两个路径都为空。
/// </summary>
internal readonly record struct WatcherEventRecord(
    WatcherEventKind Kind,
    string Path,
    string? OldPath);
