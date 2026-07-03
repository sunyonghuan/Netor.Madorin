using Netor.Cortana.Platform.Entitys.Enums;

namespace Netor.Cortana.Platform.Services.Solutions;

public sealed record SolutionManifestSummary(
    string? Id,
    string Slug,
    string Name,
    string Version,
    string? Description,
    IReadOnlyList<SolutionChildAssetSummary> Plugins,
    IReadOnlyList<SolutionChildAssetSummary> Skills,
    IReadOnlyList<SolutionChildAssetSummary> Agents,
    bool IsValid,
    string? ErrorMessage)
{
    public int PluginCount => Plugins.Count;

    public int SkillCount => Skills.Count;

    public int AgentCount => Agents.Count;

    public int TotalCount => PluginCount + SkillCount + AgentCount;

    public string CountSummary
    {
        get
        {
            var parts = new List<string>();
            if (PluginCount > 0)
            {
                parts.Add($"插件 {PluginCount}");
            }

            if (SkillCount > 0)
            {
                parts.Add($"技能 {SkillCount}");
            }

            if (AgentCount > 0)
            {
                parts.Add($"智能体 {AgentCount}");
            }

            return parts.Count == 0 ? "未声明子资源" : string.Join(" · ", parts);
        }
    }

    public IReadOnlyList<SolutionChildAssetSummary> AllAssets
        => [.. Plugins, .. Skills, .. Agents];

    public static SolutionManifestSummary Empty(string? errorMessage = null)
        => new(null, string.Empty, string.Empty, string.Empty, null, [], [], [], false, errorMessage);
}

public sealed record SolutionChildAssetSummary(
    AssetType Type,
    string TypeName,
    string Slug,
    string? Version,
    string Path,
    bool Exists = true,
    bool ManifestExists = true,
    string? ErrorMessage = null);
