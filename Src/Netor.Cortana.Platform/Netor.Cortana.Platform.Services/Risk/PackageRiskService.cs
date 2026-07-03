using System.Text.Json;
using System.IO.Compression;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Entitys.Tables.Assets;

namespace Netor.Cortana.Platform.Services.Risk;

/// <summary>
/// 资源包发布前风控检查服务。
/// </summary>
public sealed class PackageRiskService
{
    private const int MaxZipEntryCount = 500;
    private const long MaxZipEntrySize = 100 * 1024 * 1024;
    private const long MaxZipTotalUncompressedSize = 500 * 1024 * 1024;

    private static readonly string[] RiskKeywords =
    [
        "ignore previous instructions",
        "disable safety",
        "bypass approval",
        "rm -rf",
        "format c:"
    ];

    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bat",
        ".cmd",
        ".com",
        ".dll",
        ".exe",
        ".msi",
        ".ps1",
        ".scr",
        ".sh",
        ".vbs"
    };

    public PackageRiskAssessment Assess(AssetVersion version)
        => AssessMetadata(version);

    public PackageRiskAssessment AssessMetadata(AssetVersion version)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(version.PackageHash) || version.PackageHash.Length != 64)
        {
            issues.Add("包 SHA256 哈希缺失或格式不正确");
        }

        if (version.PackageSize <= 0)
        {
            issues.Add("包大小必须大于 0");
        }

        if (!version.FilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add("当前只允许发布 zip 资源包");
        }

        if (string.IsNullOrWhiteSpace(version.ManifestJson))
        {
            issues.Add("Manifest 不能为空");
        }
        else
        {
            try
            {
                JsonDocument.Parse(version.ManifestJson);
            }
            catch (JsonException)
            {
                issues.Add("Manifest JSON 格式不正确");
            }

            if (RiskKeywords.Any(keyword => version.ManifestJson.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add("Manifest 命中敏感指令风险词");
            }
        }

        return new PackageRiskAssessment(issues.Count == 0, issues);
    }

    public PackageRiskAssessment AssessPackageArchive(Stream packageStream, AssetType assetType)
    {
        ArgumentNullException.ThrowIfNull(packageStream);

        var issues = new List<string>();
        try
        {
            using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count == 0)
            {
                issues.Add("Zip 包不能为空");
            }

            if (archive.Entries.Count > MaxZipEntryCount)
            {
                issues.Add($"Zip 包文件数量超过上限 {MaxZipEntryCount}");
            }

            long totalUncompressedSize = 0;
            foreach (var entry in archive.Entries)
            {
                var normalizedName = entry.FullName.Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(normalizedName))
                {
                    issues.Add("Zip 包存在空文件名条目");
                    continue;
                }

                if (Path.IsPathRooted(normalizedName) ||
                    normalizedName.StartsWith("/", StringComparison.Ordinal) ||
                    normalizedName.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(x => x == ".."))
                {
                    issues.Add($"Zip 包存在非法路径：{entry.FullName}");
                    continue;
                }

                if (normalizedName.EndsWith("/", StringComparison.Ordinal))
                {
                    continue;
                }

                if (entry.Length > MaxZipEntrySize)
                {
                    issues.Add($"Zip 包单文件过大：{entry.FullName}");
                }

                totalUncompressedSize += entry.Length;
                if (totalUncompressedSize > MaxZipTotalUncompressedSize)
                {
                    issues.Add($"Zip 包解压后总大小超过上限 {MaxZipTotalUncompressedSize / 1024 / 1024}MB");
                    break;
                }

                var extension = Path.GetExtension(normalizedName);
                if (ShouldFlagDangerousExtension(extension, assetType))
                {
                    issues.Add($"Zip 包包含高风险文件类型：{entry.FullName}");
                }
            }
        }
        catch (InvalidDataException)
        {
            issues.Add("Zip 包结构损坏或不是有效压缩包");
        }

        return new PackageRiskAssessment(issues.Count == 0, issues);
    }

    public PackageRiskAssessment Merge(params PackageRiskAssessment[] assessments)
    {
        var issues = assessments.SelectMany(x => x.Issues).Distinct(StringComparer.Ordinal).ToList();
        return new PackageRiskAssessment(issues.Count == 0, issues);
    }

    private static bool ShouldFlagDangerousExtension(string extension, AssetType assetType)
        => DangerousExtensions.Contains(extension) && assetType is not (AssetType.Plugin or AssetType.Skill or AssetType.Solution);
}

public sealed record PackageRiskAssessment(bool Passed, IReadOnlyList<string> Issues);
