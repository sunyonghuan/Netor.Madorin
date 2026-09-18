using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Netor.Madorin.Plugin.Tests;

[TestClass]
public sealed class PluginManifestToolsTests
{
    [TestMethod]
    public void Deserialize_ReadsPluginDefaultsAndToolOverrides()
    {
        const string json = """
            {
              "id": "my_plugin",
              "name": "My Plugin",
              "version": "1.0.0",
              "runtime": "native",
              "libraryName": "MyPlugin.dll",
              "category": "development",
              "riskLevel": "Low",
              "idempotent": false,
              "tags": ["development", "automation"],
              "searchHints": ["build", "deploy"],
              "tools": [
                {
                  "name": "create_file",
                  "description": "Create a file",
                  "inputSchema": { "type": "object", "properties": { "path": { "type": "string" } }, "required": ["path"] }
                },
                {
                  "name": "delete_all",
                  "description": "Delete everything",
                  "inputSchema": { "type": "object", "properties": {} },
                  "riskLevel": "Destructive",
                  "searchHints": ["delete", "remove", "cleanup"]
                }
              ]
            }
            """;

        var manifest = JsonSerializer.Deserialize(json, PluginManifestJsonContext.Default.PluginManifest)?.Normalize();

        Assert.IsNotNull(manifest);
        Assert.AreEqual("my_plugin", manifest.Id);
        Assert.AreEqual("development", manifest.Category);
        Assert.AreEqual(ToolRiskLevel.Low, manifest.RiskLevel);
        Assert.IsFalse(manifest.Idempotent);
        Assert.HasCount(2, manifest.Tools);

        var createFile = manifest.Tools[0];
        Assert.AreEqual("create_file", createFile.Name);
        Assert.IsNull(createFile.RiskLevel);
        Assert.IsNull(createFile.SearchHints);
        Assert.IsTrue(createFile.InputSchema.HasValue);
        Assert.AreEqual("object", createFile.InputSchema.Value.GetProperty("type").GetString());

        var deleteAll = manifest.Tools[1];
        Assert.AreEqual("delete_all", deleteAll.Name);
        Assert.AreEqual(ToolRiskLevel.Destructive, deleteAll.RiskLevel);
        CollectionAssert.AreEqual(new[] { "delete", "remove", "cleanup" }, deleteAll.SearchHints!.ToArray());
    }
}
