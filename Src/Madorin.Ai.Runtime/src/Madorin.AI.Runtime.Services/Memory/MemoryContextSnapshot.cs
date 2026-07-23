namespace Madorin.AI.Runtime.Services.Memory;

public sealed record MemoryContextSnapshot(
    MemoryFileSnapshot? Global,
    MemoryFileSnapshot? Project,
    MemoryFileSnapshot? Effective)
{
    public static MemoryContextSnapshot Empty { get; } = new(null, null, null);

    public MemoryFileSnapshot? Get(MemoryScope scope) => scope switch
    {
        MemoryScope.Global => Global,
        MemoryScope.Project => Project,
        MemoryScope.Effective => Effective,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
    };

    internal static MemoryContextSnapshot Create(
        MemoryFileSnapshot? global,
        MemoryFileSnapshot? project)
    {
        var effectiveContent = (global, project) switch
        {
            (null, null) => null,
            (not null, null) => global.Content,
            (null, not null) => project.Content,
            _ => $"{global!.Content}\n---\n{project!.Content}"
        };
        var effective = effectiveContent is null
            ? null
            : new MemoryFileSnapshot(
                effectiveContent,
                MemoryFileService.ComputeHash(effectiveContent));
        return new MemoryContextSnapshot(global, project, effective);
    }
}
