using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netor.Madorin.Plugin.PluginBus;

namespace Netor.Madorin.Plugin.Process.Tests;

[TestClass]
public sealed class PluginBusFrameTests
{
    [TestMethod]
    public void SubscribeFrame_SerializesSourcePluginId()
    {
        var frame = new PluginBusSubscribeFrame
        {
            SourcePluginId = "memory_engine",
            Topics = ["conversation"],
            SubscribedOps = ["conversation.turn.completed"]
        };

        var json = JsonSerializer.Serialize(frame, PluginBusClientJsonContext.Default.PluginBusSubscribeFrame);
        using var document = JsonDocument.Parse(json);

        Assert.AreEqual("memory_engine", document.RootElement.GetProperty("sourcePluginId").GetString());
    }
}
