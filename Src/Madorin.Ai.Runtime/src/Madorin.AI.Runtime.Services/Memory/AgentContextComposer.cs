using System.Text;

namespace Madorin.AI.Runtime.Services.Memory;

public sealed class AgentContextComposer(MemoryFileService memoryFiles)
{
    private readonly MemoryFileService _memoryFiles = memoryFiles
        ?? throw new ArgumentNullException(nameof(memoryFiles));

    public async Task<AgentContextSnapshot> ComposeAsync(
        string systemPrompt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);

        var global = await _memoryFiles.ReadSnapshotAsync(MemoryScope.Global, ct)
            .ConfigureAwait(false);
        MemoryFileSnapshot? project = null;
        if (_memoryFiles.ProjectPath is not null
            && !PathsEqual(_memoryFiles.GlobalPath, _memoryFiles.ProjectPath))
        {
            project = await _memoryFiles.ReadSnapshotAsync(MemoryScope.Project, ct)
                .ConfigureAwait(false);
        }

        var instructions = new StringBuilder(systemPrompt);
        AppendMemorySection(instructions, "Global Memory", global?.Content);
        AppendMemorySection(instructions, "Project Memory", project?.Content);
        var memory = MemoryContextSnapshot.Create(global, project);
        return new AgentContextSnapshot(
            instructions.ToString(),
            global?.Hash,
            project?.Hash,
            memory);
    }

    public static AgentContextSnapshot WithoutMemory(string systemPrompt)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        return new AgentContextSnapshot(systemPrompt, null, null, MemoryContextSnapshot.Empty);
    }

    private static void AppendMemorySection(
        StringBuilder builder,
        string sectionName,
        string? content)
    {
        if (IsEmptyMemory(content))
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine();
        builder.Append("## ");
        builder.AppendLine(sectionName);
        builder.Append(content);
    }

    private static bool IsEmptyMemory(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return true;
        }

        return string.Equals(content.Trim(), "# Memory", StringComparison.Ordinal);
    }

    private static bool PathsEqual(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            comparison);
    }
}

public sealed record AgentContextSnapshot(
    string Instructions,
    string? GlobalMemoryHash,
    string? ProjectMemoryHash,
    MemoryContextSnapshot Memory);
