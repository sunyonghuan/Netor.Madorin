using Cortana.Plugins.Memory.Mcp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Memory.Test.Mcp;

[TestClass]
public sealed class MemoryMcpRuntimeOptionsLoaderTests
{
    [TestMethod]
    public void Load_Should_Default_DataDirectory_To_AppBaseData()
    {
        var oldDataDir = Environment.GetEnvironmentVariable("CORTANA_MEMORY_DATA_DIR");
        var oldAgentId = Environment.GetEnvironmentVariable("CORTANA_MEMORY_AGENT_ID");
        var oldWorkspaceId = Environment.GetEnvironmentVariable("CORTANA_MEMORY_WORKSPACE_ID");

        try
        {
            ClearEnvironmentOverrides();
            var options = MemoryMcpRuntimeOptionsLoader.Load([]);

            Assert.AreEqual(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data")), options.DataDirectory);
            Assert.AreEqual("memory.db", options.DatabaseFileName);
            Assert.AreEqual("mcp-default", options.DefaultAgentId);
            Assert.AreEqual("default", options.DefaultWorkspaceId);
            Assert.AreEqual("mcp", options.DefaultSource);
            Assert.IsTrue(options.EnableAutoProcessing);
        }
        finally
        {
            RestoreEnvironmentOverrides(oldDataDir, oldAgentId, oldWorkspaceId);
        }
    }

    [TestMethod]
    public void Load_Should_Use_Config_File_When_Provided()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cortana-memory-mcp-options", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "config.json");
        var dataDirectory = Path.Combine(directory, "portable-data");
        var oldDataDir = Environment.GetEnvironmentVariable("CORTANA_MEMORY_DATA_DIR");
        var oldAgentId = Environment.GetEnvironmentVariable("CORTANA_MEMORY_AGENT_ID");
        var oldWorkspaceId = Environment.GetEnvironmentVariable("CORTANA_MEMORY_WORKSPACE_ID");
        File.WriteAllText(configPath, $$"""
        {
          "dataDirectory": "{{dataDirectory.Replace("\\", "\\\\")}}",
          "databaseFileName": "custom.db",
          "defaultAgentId": "agent-from-file",
          "defaultWorkspaceId": "workspace-from-file",
          "defaultSource": "file-source",
          "enableAutoProcessing": false
        }
        """);

        try
        {
            ClearEnvironmentOverrides();
            var options = MemoryMcpRuntimeOptionsLoader.Load(["--config", configPath]);

            Assert.AreEqual(Path.GetFullPath(dataDirectory), options.DataDirectory);
            Assert.AreEqual("custom.db", options.DatabaseFileName);
            Assert.AreEqual("agent-from-file", options.DefaultAgentId);
            Assert.AreEqual("workspace-from-file", options.DefaultWorkspaceId);
            Assert.AreEqual("file-source", options.DefaultSource);
            Assert.IsFalse(options.EnableAutoProcessing);
        }
        finally
        {
            RestoreEnvironmentOverrides(oldDataDir, oldAgentId, oldWorkspaceId);
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Load_Should_Prefer_Cli_Over_Environment()
    {
        var oldDataDir = Environment.GetEnvironmentVariable("CORTANA_MEMORY_DATA_DIR");
        var oldAgentId = Environment.GetEnvironmentVariable("CORTANA_MEMORY_AGENT_ID");
        var oldWorkspaceId = Environment.GetEnvironmentVariable("CORTANA_MEMORY_WORKSPACE_ID");
        var envDirectory = Path.Combine(Path.GetTempPath(), "env-data");
        var cliDirectory = Path.Combine(Path.GetTempPath(), "cli-data");

        try
        {
            Environment.SetEnvironmentVariable("CORTANA_MEMORY_DATA_DIR", envDirectory);
            Environment.SetEnvironmentVariable("CORTANA_MEMORY_AGENT_ID", "agent-from-env");
            Environment.SetEnvironmentVariable("CORTANA_MEMORY_WORKSPACE_ID", "workspace-from-env");

            var options = MemoryMcpRuntimeOptionsLoader.Load([
                "--data-dir", cliDirectory,
                "--agent-id", "agent-from-cli",
                "--workspace-id", "workspace-from-cli"
            ]);

            Assert.AreEqual(Path.GetFullPath(cliDirectory), options.DataDirectory);
            Assert.AreEqual("agent-from-cli", options.DefaultAgentId);
            Assert.AreEqual("workspace-from-cli", options.DefaultWorkspaceId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CORTANA_MEMORY_DATA_DIR", oldDataDir);
            Environment.SetEnvironmentVariable("CORTANA_MEMORY_AGENT_ID", oldAgentId);
            Environment.SetEnvironmentVariable("CORTANA_MEMORY_WORKSPACE_ID", oldWorkspaceId);
        }
    }

    private static void ClearEnvironmentOverrides()
    {
        Environment.SetEnvironmentVariable("CORTANA_MEMORY_DATA_DIR", null);
        Environment.SetEnvironmentVariable("CORTANA_MEMORY_AGENT_ID", null);
        Environment.SetEnvironmentVariable("CORTANA_MEMORY_WORKSPACE_ID", null);
    }

    private static void RestoreEnvironmentOverrides(string? dataDir, string? agentId, string? workspaceId)
    {
        Environment.SetEnvironmentVariable("CORTANA_MEMORY_DATA_DIR", dataDir);
        Environment.SetEnvironmentVariable("CORTANA_MEMORY_AGENT_ID", agentId);
        Environment.SetEnvironmentVariable("CORTANA_MEMORY_WORKSPACE_ID", workspaceId);
    }
}
