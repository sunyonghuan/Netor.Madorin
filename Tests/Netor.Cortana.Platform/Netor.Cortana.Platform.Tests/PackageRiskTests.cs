using System.IO.Compression;
using Netor.Cortana.Platform.Entitys.Enums;
using Netor.Cortana.Platform.Services.Risk;

namespace Netor.Cortana.Platform.Tests;

public sealed class PackageRiskTests
{
    [Fact]
    public void AssessPackageArchive_WithValidZip_ReturnsPassed()
    {
        var service = new PackageRiskService();
        using var stream = CreateZip(("manifest.json", "{}"), ("content/readme.txt", "ok"));

        var result = service.AssessPackageArchive(stream, AssetType.Skill);

        Assert.True(result.Passed);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void AssessPackageArchive_WithPathTraversal_ReturnsRiskIssue()
    {
        var service = new PackageRiskService();
        using var stream = CreateZip(("../evil.txt", "bad"));

        var result = service.AssessPackageArchive(stream, AssetType.Skill);

        Assert.False(result.Passed);
        Assert.Contains(result.Issues, x => x.Contains("非法路径", StringComparison.Ordinal));
    }

    [Fact]
    public void AssessPackageArchive_WithSkillScript_ReturnsPassed()
    {
        var service = new PackageRiskService();
        using var stream = CreateZip(("tools/install.ps1", "Write-Host bad"));

        var result = service.AssessPackageArchive(stream, AssetType.Skill);

        Assert.True(result.Passed);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void AssessPackageArchive_WithPluginExecutablePayload_ReturnsPassed()
    {
        var service = new PackageRiskService();
        using var stream = CreateZip(
            ("bin/plugin.dll", "ok"),
            ("bin/plugin.exe", "ok"),
            ("tools/install.cmd", "ok"),
            ("tools/install.ps1", "ok"));

        var result = service.AssessPackageArchive(stream, AssetType.Plugin);

        Assert.True(result.Passed);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void AssessPackageArchive_WithSkillDll_ReturnsPassed()
    {
        var service = new PackageRiskService();
        using var stream = CreateZip(("bin/plugin.dll", "ok"));

        var result = service.AssessPackageArchive(stream, AssetType.Skill);

        Assert.True(result.Passed);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void AssessPackageArchive_WithSolutionBundledPluginAndSkillResources_ReturnsPassed()
    {
        var service = new PackageRiskService();
        using var stream = CreateZip(
            ("plugins/process/bin/process.dll", "ok"),
            ("plugins/process/bin/process.exe", "ok"),
            ("skills/deploy/tools/install.cmd", "ok"),
            ("skills/deploy/tools/install.ps1", "ok"));

        var result = service.AssessPackageArchive(stream, AssetType.Solution);

        Assert.True(result.Passed);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void AssessPackageArchive_WithAgentDangerousExtension_ReturnsRiskIssue()
    {
        var service = new PackageRiskService();
        using var stream = CreateZip(("tools/install.ps1", "Write-Host bad"));

        var result = service.AssessPackageArchive(stream, AssetType.Agent);

        Assert.False(result.Passed);
        Assert.Contains(result.Issues, x => x.Contains("高风险文件类型", StringComparison.Ordinal));
    }

    private static MemoryStream CreateZip(params (string Name, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }
}
