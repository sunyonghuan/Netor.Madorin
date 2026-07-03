using Microsoft.VisualStudio.TestTools.UnitTesting;
using Netor.Cortana.Plugin;

namespace Netor.Cortana.Plugin.Process.Tests;

[TestClass]
public sealed class PluginSettingsTests
{
    [TestMethod]
    public void FromJson_WithPluginBusExtensions_Should_PopulateCompatibilityEndpoints()
    {
        var settings = PluginSettings.FromJson("""
            {
              "dataDirectory": "data",
              "workspaceDirectory": "workspace",
              "pluginDirectory": "plugin",
              "wsPort": 12841,
              "extensions": {
                "pluginBusEndpoint": "ws://localhost:12841/internal",
                "pluginBusPort": "12841",
                "pluginBusProtocol": "cortana.plugin-bus",
                "pluginBusVersion": "1.2.0"
              }
            }
            """);

        Assert.AreEqual("ws://localhost:12841/internal", settings.ChatWsEndpoint);
        Assert.AreEqual("ws://localhost:12841/internal", settings.ConversationFeedEndpoint);
        Assert.AreEqual("cortana.plugin-bus", settings.ConversationFeedProtocol);
        Assert.AreEqual("1.2.0", settings.ConversationFeedVersion);
        Assert.AreEqual(12841, settings.ConversationFeedPort);
    }

    [TestMethod]
    public void FromJson_WithLegacyExtensions_Should_PopulateEndpoints()
    {
        var settings = PluginSettings.FromJson("""
            {
              "dataDirectory": "data",
              "workspaceDirectory": "workspace",
              "pluginDirectory": "plugin",
              "wsPort": 12841,
              "extensions": {
                "chatWsEndpoint": "ws://localhost:12841/ws/",
                "conversationFeedEndpoint": "ws://localhost:12841/internal/conversation-feed/",
                "conversationFeedPort": "12842",
                "conversationFeedProtocol": "conversation-feed",
                "conversationFeedVersion": "1.0.0"
              }
            }
            """);

        Assert.AreEqual("ws://localhost:12841/ws/", settings.ChatWsEndpoint);
        Assert.AreEqual("ws://localhost:12841/internal/conversation-feed/", settings.ConversationFeedEndpoint);
        Assert.AreEqual("conversation-feed", settings.ConversationFeedProtocol);
        Assert.AreEqual("1.0.0", settings.ConversationFeedVersion);
        Assert.AreEqual(12842, settings.ConversationFeedPort);
    }

    [TestMethod]
    public void FromJson_WithBothNewAndLegacyExtensions_Should_PreferPluginBusExtensions()
    {
        var settings = PluginSettings.FromJson("""
            {
              "dataDirectory": "data",
              "workspaceDirectory": "workspace",
              "pluginDirectory": "plugin",
              "wsPort": 12841,
              "extensions": {
                "pluginBusEndpoint": "ws://localhost:12841/internal",
                "pluginBusPort": "12841",
                "pluginBusProtocol": "cortana.plugin-bus",
                "pluginBusVersion": "1.2.0",
                "chatWsEndpoint": "ws://localhost:12841/ws/",
                "conversationFeedEndpoint": "ws://localhost:12841/internal/conversation-feed/",
                "conversationFeedPort": "12842",
                "conversationFeedProtocol": "conversation-feed",
                "conversationFeedVersion": "1.0.0"
              }
            }
            """);

        Assert.AreEqual("ws://localhost:12841/internal", settings.ChatWsEndpoint);
        Assert.AreEqual("ws://localhost:12841/internal", settings.ConversationFeedEndpoint);
        Assert.AreEqual("cortana.plugin-bus", settings.ConversationFeedProtocol);
        Assert.AreEqual("1.2.0", settings.ConversationFeedVersion);
        Assert.AreEqual(12841, settings.ConversationFeedPort);
    }
}
