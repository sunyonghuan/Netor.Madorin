using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Netor.Cortana.Platform.Entitys.Enums;

namespace Netor.Cortana.Platform.Services.Solutions;

public static class SolutionManifestParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static SolutionManifestSummary ParseManifestJson(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return SolutionManifestSummary.Empty("solution.json 为空。");
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<SolutionPackageManifest>(manifestJson, JsonOptions);
            return CreateSummary(manifest, validateAssets: false, entryNames: null);
        }
        catch (JsonException)
        {
            return SolutionManifestSummary.Empty("solution.json JSON 格式不正确。");
        }
    }

    public static SolutionManifestSummary ParseArchive(ZipArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var manifestEntry = FindEntryByPath(archive, "solution.json")
            ?? throw new InvalidDataException("解决方案资源包中未找到 solution.json。");
        var manifestJson = ReadEntryText(manifestEntry);
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            throw new InvalidDataException("solution.json 不能为空。");
        }

        SolutionPackageManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SolutionPackageManifest>(manifestJson, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("solution.json JSON 格式不正确。", ex);
        }

        var entryNames = archive.Entries
            .Select(static entry => NormalizeZipPath(entry.FullName))
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var summary = CreateSummary(manifest, validateAssets: true, entryNames);
        if (!summary.IsValid)
        {
            throw new InvalidDataException(summary.ErrorMessage ?? "solution.json 校验失败。");
        }

        return summary;
    }

    public static string NormalizeManifestJson(string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson);
        return JsonSerializer.Serialize(document.RootElement, SolutionManifestJsonContext.Default.JsonElement);
    }

    private static SolutionManifestSummary CreateSummary(
        SolutionPackageManifest? manifest,
        bool validateAssets,
        IReadOnlySet<string>? entryNames)
    {
        if (manifest is null)
        {
            return SolutionManifestSummary.Empty("solution.json 反序列化为空。");
        }

        if (string.IsNullOrWhiteSpace(manifest.Slug))
        {
            return SolutionManifestSummary.Empty("solution.json 缺少 slug。");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            return SolutionManifestSummary.Empty("solution.json 缺少 version。");
        }

        var plugins = CreateChildSummaries(AssetType.Plugin, "插件", "plugins", "plugin.json", manifest.Assets?.Plugins, validateAssets, entryNames);
        var skills = CreateChildSummaries(AssetType.Skill, "技能", "skills", "skill.md", manifest.Assets?.Skills, validateAssets, entryNames);
        var agents = CreateChildSummaries(AssetType.Agent, "智能体", "agents", "agent.json", manifest.Assets?.Agents, validateAssets, entryNames);
        var allAssets = plugins.Concat(skills).Concat(agents).ToList();
        if (allAssets.Count == 0)
        {
            var emptySlug = manifest.Slug.Trim();
            return new SolutionManifestSummary(
                manifest.Id,
                emptySlug,
                string.IsNullOrWhiteSpace(manifest.Name) ? emptySlug : manifest.Name.Trim(),
                manifest.Version.Trim(),
                manifest.Description,
                plugins,
                skills,
                agents,
                false,
                "解决方案至少需要包含一种插件、技能或智能体资源。");
        }

        var invalid = allAssets.FirstOrDefault(x => !x.Exists || !x.ManifestExists || !string.IsNullOrWhiteSpace(x.ErrorMessage));
        return new SolutionManifestSummary(
            manifest.Id,
            manifest.Slug.Trim(),
            string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Slug.Trim() : manifest.Name.Trim(),
            manifest.Version.Trim(),
            manifest.Description,
            plugins,
            skills,
            agents,
            invalid is null,
            invalid?.ErrorMessage);
    }

    private static List<SolutionChildAssetSummary> CreateChildSummaries(
        AssetType type,
        string typeName,
        string folderName,
        string manifestFileName,
        IReadOnlyList<SolutionPackageAsset>? assets,
        bool validateAssets,
        IReadOnlySet<string>? entryNames)
    {
        var result = new List<SolutionChildAssetSummary>();
        foreach (var asset in assets ?? [])
        {
            var slug = asset.Slug?.Trim() ?? string.Empty;
            var path = GetAssetPath(asset, folderName);
            var error = ValidateRelativePath(path);
            if (string.IsNullOrWhiteSpace(slug))
            {
                error = "解决方案子资源缺少 slug。";
            }

            var exists = true;
            var manifestExists = true;
            if (validateAssets && string.IsNullOrWhiteSpace(error) && entryNames is not null)
            {
                exists = HasEntryUnderDirectory(entryNames, path);
                manifestExists = entryNames.Contains($"{path}/{manifestFileName}");
                if (!exists)
                {
                    error = $"{typeName}子资源目录不存在：{path}";
                }
                else if (!manifestExists)
                {
                    error = $"{typeName}子资源 {slug} 缺少 {manifestFileName}。";
                }
            }

            result.Add(new SolutionChildAssetSummary(
                type,
                typeName,
                slug,
                asset.Version,
                path,
                exists,
                manifestExists,
                error));
        }

        return result;
    }

    private static string GetAssetPath(SolutionPackageAsset asset, string folderName)
        => NormalizeZipPath(string.IsNullOrWhiteSpace(asset.Path) ? $"{folderName}/{asset.Slug}" : asset.Path);

    private static string? ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "解决方案子资源路径不能为空。";
        }

        if (Path.IsPathFullyQualified(path) ||
            path.StartsWith("/", StringComparison.Ordinal) ||
            path.Split('/').Any(static part => string.IsNullOrWhiteSpace(part) || part is "." or ".."))
        {
            return $"解决方案子资源路径非法：{path}";
        }

        return null;
    }

    private static bool HasEntryUnderDirectory(IReadOnlySet<string> entryNames, string directory)
        => entryNames.Any(name => name.StartsWith(directory.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));

    private static ZipArchiveEntry? FindEntryByPath(ZipArchive archive, string fileName)
    {
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(Path.GetFileName(entry.FullName), fileName, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream);
        return reader.ReadToEnd();
    }

    private static string NormalizeZipPath(string? value)
        => (value ?? string.Empty).Replace('\\', '/').Trim('/');
}

internal sealed record SolutionPackageManifest(
    string? Id,
    string? Slug,
    string? Name,
    string? Version,
    string? Description,
    SolutionPackageAssets? Assets);

internal sealed record SolutionPackageAssets(
    IReadOnlyList<SolutionPackageAsset>? Plugins,
    IReadOnlyList<SolutionPackageAsset>? Skills,
    IReadOnlyList<SolutionPackageAsset>? Agents);

internal sealed record SolutionPackageAsset(
    string? Slug,
    string? Version,
    string? Path);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class SolutionManifestJsonContext : JsonSerializerContext;
