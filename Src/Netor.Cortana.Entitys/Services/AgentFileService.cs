using Microsoft.Extensions.Logging;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 文件版 Agent CRUD 服务。
/// </summary>
public sealed class AgentFileService(
    IAppPaths appPaths,
    AgentManifestSerializer serializer,
    AgentManifestValidator validator,
    AgentFileIndex index,
    ILogger<AgentFileService> logger)
{
    public const string ManifestFileName = "manifest.yaml";
    public const string PromptFileName = "prompt.md";

    public string AgentsDirectory => appPaths.UserAgentsDirectory;

    public IReadOnlyList<AgentFileRecord> GetAll() => index.GetAll();

    public AgentFileRecord? GetByName(string name) => index.GetByName(name);

    public void RebuildIndex()
    {
        index.Rebuild(AgentsDirectory);
    }

    public void Save(AgentManifest manifest, string prompt)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var validation = validator.Validate(manifest, manifest.Name);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(validation.Error);
        }

        var directory = Path.Combine(AgentsDirectory, manifest.Name);
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, ManifestFileName), serializer.Serialize(manifest));
        File.WriteAllText(Path.Combine(directory, PromptFileName), prompt ?? string.Empty);
        index.RefreshDirectory(directory);
    }

    public bool SoftDelete(string name)
    {
        var record = GetByName(name);
        if (record is null)
        {
            return false;
        }

        if (string.Equals(record.Manifest.Kind, AgentManifestKinds.BuiltinSystem, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("builtin/system Agent 不允许删除。");
        }

        var trashDirectory = Path.Combine(AgentsDirectory, ".trash");
        Directory.CreateDirectory(trashDirectory);
        var target = Path.Combine(trashDirectory, $"{record.Manifest.Name}-{DateTimeOffset.Now:yyyyMMddHHmmssfff}");
        Directory.Move(record.DirectoryPath, target);
        index.Remove(record.Manifest.Name);
        logger.LogInformation("Agent 已软删到 {Path}", target);
        return true;
    }
}
