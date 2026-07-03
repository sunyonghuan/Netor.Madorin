using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

using Netor.Cortana.AI.Providers;

namespace Netor.Cortana.AI.Tests.Delegation;

[TestClass]
public sealed class ToolMountFilteringContextProviderTests
{
    [TestMethod]
    public void FilterTools_KeepsOnlyMountedTools()
    {
        var result = ToolMountFilteringContextProvider.FilterTools(
            [
                AIFunctionFactory.Create(name: "sys_read_file", method: () => "read"),
                AIFunctionFactory.Create(name: "sys_write_file", method: () => "write")
            ],
            new HashSet<string>(["sys_read_file"], StringComparer.OrdinalIgnoreCase));

        Assert.HasCount(1, result);
        Assert.AreEqual("sys_read_file", result[0].Name);
    }

    [TestMethod]
    public void FilterTools_WithEmptyMounts_RemovesAllTools()
    {
        var result = ToolMountFilteringContextProvider.FilterTools(
            [AIFunctionFactory.Create(name: "sys_read_file", method: () => "read")],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void FilterTools_KeepsPowerShellTools_WhenExplicitlyMounted()
    {
        var result = ToolMountFilteringContextProvider.FilterTools(
            [
                AIFunctionFactory.Create(name: "sys_execute_powershell", method: () => "ps"),
                AIFunctionFactory.Create(name: "sys_send_command", method: () => "session"),
                AIFunctionFactory.Create(name: "sys_write_file", method: () => "write")
            ],
            new HashSet<string>(
                ["sys_execute_powershell", "sys_send_command"],
                StringComparer.OrdinalIgnoreCase));

        CollectionAssert.AreEqual(
            new[] { "sys_execute_powershell", "sys_send_command" },
            result.Select(static tool => tool.Name).ToArray());
    }

    [TestMethod]
    public void FilterTools_KeepsPluginAndMcpTools_WhenExplicitlyMounted()
    {
        var result = ToolMountFilteringContextProvider.FilterTools(
            [
                AIFunctionFactory.Create(name: "plugin_generate_report", method: () => "plugin"),
                AIFunctionFactory.Create(name: "mcp_query_issue", method: () => "mcp"),
                AIFunctionFactory.Create(name: "sys_read_file", method: () => "file")
            ],
            new HashSet<string>(
                ["plugin_generate_report", "mcp_query_issue"],
                StringComparer.OrdinalIgnoreCase));

        CollectionAssert.AreEqual(
            new[] { "plugin_generate_report", "mcp_query_issue" },
            result.Select(static tool => tool.Name).ToArray());
    }

    [TestMethod]
    public void FilterTools_DropsUnmountedPluginAndMcpTools()
    {
        var result = ToolMountFilteringContextProvider.FilterTools(
            [
                AIFunctionFactory.Create(name: "plugin_generate_report", method: () => "plugin"),
                AIFunctionFactory.Create(name: "mcp_query_issue", method: () => "mcp"),
                AIFunctionFactory.Create(name: "sys_read_file", method: () => "file")
            ],
            new HashSet<string>(["sys_read_file"], StringComparer.OrdinalIgnoreCase));

        CollectionAssert.AreEqual(
            new[] { "sys_read_file" },
            result.Select(static tool => tool.Name).ToArray());
    }
}
