namespace Netor.Cortana.Platform.Admin.Models.Shared;

/// <summary>
/// LayUI table 组件要求的数据返回结构。
/// </summary>
public sealed class LayuiTableResult<T>
{
    public int Code { get; init; }

    public string Msg { get; init; } = string.Empty;

    public int Count { get; init; }

    public IReadOnlyList<T> Data { get; init; } = [];
}
