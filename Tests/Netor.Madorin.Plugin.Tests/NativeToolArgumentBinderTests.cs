using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Madorin.Plugin.Native;

namespace Netor.Madorin.Plugin.Tests;

[TestClass]
public sealed class NativeToolArgumentBinderTests
{
    private static readonly NativeToolParameter[] RecallParameters =
    [
        new() { Name = "query_text", Type = "string", Required = true },
        new() { Name = "query_intent", Type = "string", Required = true },
        new() { Name = "agent_id", Type = "string", Required = true },
        new() { Name = "workspace_id", Type = "string", Required = true },
        new() { Name = "max_memory_count", Type = "integer", Required = true }
    ];

    [TestMethod]
    public void Normalize_UnwrapsArgsJsonAndCamelCase_AndFillsMissingKeys()
    {
        var json = NativeToolArgumentBinder.Normalize(
            """{"argsJson":"{\"queryText\":\"hello\",\"maxMemoryCount\":\"3\"}"}""",
            RecallParameters);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.AreEqual("hello", root.GetProperty("query_text").GetString());
        Assert.AreEqual(string.Empty, root.GetProperty("query_intent").GetString());
        Assert.AreEqual(string.Empty, root.GetProperty("agent_id").GetString());
        Assert.AreEqual(string.Empty, root.GetProperty("workspace_id").GetString());
        Assert.AreEqual(3, root.GetProperty("max_memory_count").GetInt32());
        Assert.IsFalse(root.TryGetProperty("argsJson", out _));
    }

    [TestMethod]
    public void Normalize_EmptyObject_DoesNotThrow_AndSuppliesDefaults()
    {
        var json = NativeToolArgumentBinder.Normalize("{}", RecallParameters);
        using var document = JsonDocument.Parse(json);
        Assert.AreEqual(string.Empty, document.RootElement.GetProperty("query_text").GetString());
        Assert.AreEqual(0, document.RootElement.GetProperty("max_memory_count").GetInt32());
    }

    [TestMethod]
    public void Wrapper_PublishesParameterSchema_NotArgsJson()
    {
        var info = new NativePluginInfo
        {
            Id = "memory_engine",
            Name = "增强记忆引擎",
            Version = "1.0.0",
            Tools =
            [
                new NativeToolInfo
                {
                    Name = "memory_engine_memory_recall",
                    ShortName = "memory_recall",
                    Description = "召回",
                    Parameters = [.. RecallParameters]
                }
            ]
        };

        var wrapper = new NativePluginWrapper(new FakeHost(), info);
        var function = Assert.IsInstanceOfType<AIFunction>(wrapper.Tools[0]);
        Assert.AreEqual("memory_recall", function.Name);
        Assert.IsTrue(function.JsonSchema.GetProperty("properties").TryGetProperty("query_text", out _));
        Assert.IsFalse(function.JsonSchema.GetProperty("properties").TryGetProperty("argsJson", out _));
    }

    private sealed class FakeHost : ExternalProcessPluginHostBase
    {
        public FakeHost()
            : base(
                Path.GetTempPath(),
                new PluginManifest
                {
                    Id = "memory_engine",
                    Name = "memory",
                    Version = "1.0.0",
                    Runtime = PluginRuntime.Native
                },
                NullLogger<FakeHost>.Instance,
                new StubConfig())
        {
        }

        protected override System.Diagnostics.ProcessStartInfo CreateProcessStartInfo()
            => new() { FileName = "cmd.exe" };
    }

    private sealed class StubConfig : IPluginRuntimeConfigProvider
    {
        public string BuildPluginConfigJson(string pluginId, PluginManifest manifest) => "{}";

        public string BuildHostCapabilityGrantsJson(string pluginId, PluginManifest manifest) => "{}";
    }
}
