using Microsoft.Extensions.Logging;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 文件版 Agent 的内存索引，负责启动扫描和按名称查询。
/// </summary>
public sealed class AgentFileIndex(
    AgentManifestSerializer serializer,
    AgentManifestValidator validator,
    ILogger<AgentFileIndex> logger)
{
    private readonly object _gate = new();
    private Dictionary<string, AgentFileRecord> _records = new(StringComparer.Ordinal);

    public IReadOnlyList<AgentFileRecord> GetAll()
    {
        lock (_gate)
        {
            return _records.Values
                .OrderBy(static x => x.Manifest.SortOrder)
                .ThenBy(static x => x.Manifest.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public AgentFileRecord? GetByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        lock (_gate)
        {
            return _records.GetValueOrDefault(name);
        }
    }

    public void Rebuild(string agentsDirectory)
    {
        Directory.CreateDirectory(agentsDirectory);

        var next = new Dictionary<string, AgentFileRecord>(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(agentsDirectory))
        {
            var directoryName = Path.GetFileName(directory);
            if (string.Equals(directoryName, ".trash", StringComparison.Ordinal))
            {
                continue;
            }

            var record = LoadDirectory(directory);
            if (record is not null)
            {
                next[record.Manifest.Name] = record;
            }
        }

        lock (_gate)
        {
            _records = next;
        }
    }

    public void RefreshDirectory(string directory)
    {
        var directoryName = Path.GetFileName(directory);
        if (string.IsNullOrWhiteSpace(directoryName) ||
            string.Equals(directoryName, ".trash", StringComparison.Ordinal))
        {
            return;
        }

        var record = LoadDirectory(directory);
        lock (_gate)
        {
            _records.Remove(directoryName);
            if (record is not null)
            {
                _records[record.Manifest.Name] = record;
            }
        }
    }

    public void Remove(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        lock (_gate)
        {
            _records.Remove(name);
        }
    }

    private AgentFileRecord? LoadDirectory(string directory)
    {
        var manifestPath = Path.Combine(directory, AgentFileService.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var yaml = File.ReadAllText(manifestPath);
            var manifest = serializer.Deserialize(yaml);
            var result = validator.Validate(manifest, Path.GetFileName(directory));
            if (!result.IsValid)
            {
                logger.LogWarning("Agent manifest 校验失败（{Error}）：{Path}", result.Error, manifestPath);
                return null;
            }

            var promptPath = Path.Combine(directory, AgentFileService.PromptFileName);
            var prompt = File.Exists(promptPath) ? File.ReadAllText(promptPath) : string.Empty;
            return new AgentFileRecord(manifest, directory, manifestPath, promptPath, prompt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogWarning(ex, "Agent manifest 加载失败：{Path}", manifestPath);
            return null;
        }
    }
}
