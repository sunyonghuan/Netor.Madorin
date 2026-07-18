using Microsoft.Extensions.AI;

using Netor.Cortana.AI;
using Netor.Cortana.AI.Providers;

namespace Netor.Cortana.AI.Tests.Providers;

[TestClass]
public sealed class ProviderToolLimitContextProviderTests
{
    [TestMethod]
    public void ApplyLimit_WhenMaxToolsIsZero_KeepsAllTools()
    {
        var tools = CreateTools(3);

        var result = ProviderToolLimitContextProvider.ApplyLimit(tools, 0);

        Assert.IsFalse(result.WasLimited);
        Assert.HasCount(3, result.Tools);
        Assert.IsEmpty(result.RemovedToolNames);
    }

    [TestMethod]
    public void ApplyLimit_WhenToolCountIsWithinLimit_KeepsAllTools()
    {
        var tools = CreateTools(3);

        var result = ProviderToolLimitContextProvider.ApplyLimit(tools, 5);

        Assert.IsFalse(result.WasLimited);
        Assert.HasCount(3, result.Tools);
        CollectionAssert.AreEqual(new[] { "tool_0", "tool_1", "tool_2" }, result.Tools.Select(static tool => tool.Name).ToArray());
    }

    [TestMethod]
    public void ApplyLimit_WhenToolCountExceedsLimit_KeepsDeterministicPrefix()
    {
        var tools = CreateTools(5);

        var result = ProviderToolLimitContextProvider.ApplyLimit(tools, 3);

        Assert.IsTrue(result.WasLimited);
        Assert.AreEqual(5, result.OriginalToolCount);
        Assert.AreEqual(3, result.MaxTools);
        CollectionAssert.AreEqual(new[] { "tool_0", "tool_1", "tool_2" }, result.Tools.Select(static tool => tool.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "tool_3", "tool_4" }, result.RemovedToolNames.ToArray());
    }

    [TestMethod]
    public void ProviderMaxToolsConfigurationNames_AreStableForKimi()
    {
        Assert.AreEqual("AI.Provider.Kimi.MaxTools", AIAgentFactory.BuildProviderMaxToolsSettingKey("Kimi"));
        Assert.AreEqual("CORTANA_KIMI_MAX_TOOLS", AIAgentFactory.BuildProviderMaxToolsEnvironmentName("Kimi"));
    }

    private static IReadOnlyList<AIFunction> CreateTools(int count)
    {
        var tools = new List<AIFunction>(count);
        for (var i = 0; i < count; i++)
        {
            var value = i;
            tools.Add(AIFunctionFactory.Create(name: $"tool_{i}", method: () => value));
        }

        return tools;
    }
}
