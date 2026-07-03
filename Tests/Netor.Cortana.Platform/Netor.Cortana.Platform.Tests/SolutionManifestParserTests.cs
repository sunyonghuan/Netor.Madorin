using System.IO.Compression;
using Netor.Cortana.Platform.Services.Solutions;

namespace Netor.Cortana.Platform.Tests;

public sealed class SolutionManifestParserTests
{
    [Fact]
    public void ParseManifestJson_WithPluginOnlySolution_ReturnsValidSummary()
    {
        var summary = SolutionManifestParser.ParseManifestJson("""
            {
              "name": "Process Toolkit",
              "slug": "process-toolkit",
              "version": "1.0.0",
              "assets": {
                "plugins": [
                  { "slug": "process", "version": "1.0.0", "path": "plugins/process" }
                ]
              }
            }
            """);

        Assert.True(summary.IsValid);
        Assert.Equal(1, summary.PluginCount);
        Assert.Equal(0, summary.SkillCount);
        Assert.Equal(0, summary.AgentCount);
        Assert.Equal("插件 1", summary.CountSummary);
    }

    [Fact]
    public void ParseManifestJson_WithTwoAssetTypes_ReturnsValidSummary()
    {
        var summary = SolutionManifestParser.ParseManifestJson("""
            {
              "name": "Deploy Suite",
              "slug": "deploy-suite",
              "version": "2.0.0",
              "assets": {
                "plugins": [
                  { "slug": "process", "version": "1.0.0", "path": "plugins/process" }
                ],
                "skills": [
                  { "slug": "deploy", "version": "1.1.0", "path": "skills/deploy" }
                ]
              }
            }
            """);

        Assert.True(summary.IsValid);
        Assert.Equal(2, summary.TotalCount);
        Assert.Equal("插件 1 · 技能 1", summary.CountSummary);
    }

    [Fact]
    public void ParseArchive_WithAllAssetTypesAndRequiredManifests_ReturnsValidSummary()
    {
        using var stream = CreateZip(
            ("solution.json", """
                {
                  "name": "Ops Solution",
                  "slug": "ops-solution",
                  "version": "3.0.0",
                  "assets": {
                    "plugins": [{ "slug": "process", "version": "1.0.0", "path": "plugins/process" }],
                    "skills": [{ "slug": "deploy", "version": "1.0.0", "path": "skills/deploy" }],
                    "agents": [{ "slug": "ops-agent", "version": "1.0.0", "path": "agents/ops-agent" }]
                  }
                }
                """),
            ("plugins/process/plugin.json", "{}"),
            ("skills/deploy/skill.md", "# Deploy"),
            ("agents/ops-agent/agent.json", "{}"));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var summary = SolutionManifestParser.ParseArchive(archive);

        Assert.True(summary.IsValid);
        Assert.Equal(3, summary.TotalCount);
        Assert.Equal("插件 1 · 技能 1 · 智能体 1", summary.CountSummary);
    }

    [Fact]
    public void ParseManifestJson_WithEmptySolution_ReturnsInvalidSummary()
    {
        var summary = SolutionManifestParser.ParseManifestJson("""
            {
              "name": "Empty",
              "slug": "empty",
              "version": "1.0.0",
              "assets": {}
            }
            """);

        Assert.False(summary.IsValid);
        Assert.Contains("至少需要包含一种", summary.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseArchive_WithMissingChildDirectory_ThrowsInvalidDataException()
    {
        using var stream = CreateZip(("solution.json", """
            {
              "name": "Broken",
              "slug": "broken",
              "version": "1.0.0",
              "assets": {
                "plugins": [{ "slug": "process", "version": "1.0.0", "path": "plugins/process" }]
              }
            }
            """));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var exception = Assert.Throws<InvalidDataException>(() => SolutionManifestParser.ParseArchive(archive));
        Assert.Contains("目录不存在", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseArchive_WithoutSolutionManifest_ThrowsInvalidDataException()
    {
        using var stream = CreateZip(("plugins/process/plugin.json", "{}"));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var exception = Assert.Throws<InvalidDataException>(() => SolutionManifestParser.ParseArchive(archive));
        Assert.Contains("未找到 solution.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseArchive_WithMissingChildManifest_ThrowsInvalidDataException()
    {
        using var stream = CreateZip(
            ("solution.json", """
                {
                  "name": "Broken",
                  "slug": "broken",
                  "version": "1.0.0",
                  "assets": {
                    "skills": [{ "slug": "deploy", "version": "1.0.0", "path": "skills/deploy" }]
                  }
                }
                """),
            ("skills/deploy/readme.txt", "missing skill.md"));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var exception = Assert.Throws<InvalidDataException>(() => SolutionManifestParser.ParseArchive(archive));
        Assert.Contains("缺少 skill.md", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseArchive_WithInvalidChildPath_ThrowsInvalidDataException()
    {
        using var stream = CreateZip(("solution.json", """
            {
              "name": "Broken",
              "slug": "broken",
              "version": "1.0.0",
              "assets": {
                "plugins": [{ "slug": "process", "version": "1.0.0", "path": "../plugins/process" }]
              }
            }
            """));
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var exception = Assert.Throws<InvalidDataException>(() => SolutionManifestParser.ParseArchive(archive));
        Assert.Contains("路径非法", exception.Message, StringComparison.Ordinal);
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
