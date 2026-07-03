using Microsoft.Extensions.Logging;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 从工厂 agents-seed 目录拷贝内置 Agent 到用户 agents 目录。
/// </summary>
public sealed class AgentSeedFromFactoryService(
    IAppPaths appPaths,
    AgentManifestSerializer serializer,
    ILogger<AgentSeedFromFactoryService> logger)
{
    private const string FactoryAgentsDirectoryName = "agents-seed";
    private const string DefaultAgentName = "default";
    private const string LegacyDefaultDisplayName = "默认助手";

    public string FactoryAgentsDirectory => ResolveFactoryAgentsDirectory();

    public void EnsureSeedAgents()
    {
        Directory.CreateDirectory(appPaths.UserAgentsDirectory);
        if (!Directory.Exists(FactoryAgentsDirectory))
        {
            logger.LogInformation("Agent 工厂目录不存在，跳过拷贝：{Path}", FactoryAgentsDirectory);
            return;
        }

        foreach (var sourceDirectory in Directory.EnumerateDirectories(FactoryAgentsDirectory))
        {
            var name = Path.GetFileName(sourceDirectory);
            var targetDirectory = Path.Combine(appPaths.UserAgentsDirectory, name);
            if (Directory.Exists(targetDirectory))
            {
                BackfillSeedAssets(sourceDirectory, targetDirectory);
                continue;
            }

            CopyDirectory(sourceDirectory, targetDirectory);
        }
    }

    private string ResolveFactoryAgentsDirectory()
    {
        var applicationDirectory = Path.Combine(AppContext.BaseDirectory, FactoryAgentsDirectoryName);
        if (Directory.Exists(applicationDirectory))
        {
            return applicationDirectory;
        }

        var userDataDirectory = Path.Combine(appPaths.UserDataDirectory, FactoryAgentsDirectoryName);
        return Directory.Exists(userDataDirectory) ? userDataDirectory : applicationDirectory;
    }

    private void BackfillSeedAssets(string sourceDirectory, string targetDirectory)
    {
        var sourceManifestPath = Path.Combine(sourceDirectory, AgentFileService.ManifestFileName);
        var targetManifestPath = Path.Combine(targetDirectory, AgentFileService.ManifestFileName);
        if (!File.Exists(sourceManifestPath) || !File.Exists(targetManifestPath))
        {
            return;
        }

        try
        {
            var sourceManifest = serializer.Deserialize(File.ReadAllText(sourceManifestPath));
            var targetManifest = serializer.Deserialize(File.ReadAllText(targetManifestPath));
            var updated = BackfillSeedDisplayName(sourceManifest, targetManifest);

            if (!string.IsNullOrWhiteSpace(sourceManifest.Avatar) &&
                string.IsNullOrWhiteSpace(targetManifest.Avatar) &&
                !Path.IsPathRooted(sourceManifest.Avatar))
            {
                var sourceAvatarPath = ResolveContainedPath(sourceDirectory, sourceManifest.Avatar);
                var targetAvatarPath = ResolveContainedPath(targetDirectory, sourceManifest.Avatar);
                if (sourceAvatarPath is not null && targetAvatarPath is not null && File.Exists(sourceAvatarPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetAvatarPath)!);
                    if (!File.Exists(targetAvatarPath))
                    {
                        File.Copy(sourceAvatarPath, targetAvatarPath, overwrite: false);
                    }
                    targetManifest.Avatar = sourceManifest.Avatar;
                    updated = true;
                }
            }

            if (updated)
            {
                File.WriteAllText(targetManifestPath, serializer.Serialize(targetManifest));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogWarning(ex, "回填 Agent 种子资源失败：{Path}", targetDirectory);
        }
    }

    private static bool BackfillSeedDisplayName(AgentManifest sourceManifest, AgentManifest targetManifest)
    {
        if (!string.Equals(sourceManifest.Name, DefaultAgentName, StringComparison.Ordinal) ||
            !string.Equals(targetManifest.Name, DefaultAgentName, StringComparison.Ordinal) ||
            !string.Equals(targetManifest.DisplayName, LegacyDefaultDisplayName, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(sourceManifest.DisplayName))
        {
            return false;
        }

        targetManifest.DisplayName = sourceManifest.DisplayName;
        return true;
    }

    private static string? ResolveContainedPath(string rootDirectory, string relativePath)
    {
        var root = Path.GetFullPath(rootDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(targetDirectory, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            CopyDirectory(directory, Path.Combine(targetDirectory, Path.GetFileName(directory)));
        }
    }
}
