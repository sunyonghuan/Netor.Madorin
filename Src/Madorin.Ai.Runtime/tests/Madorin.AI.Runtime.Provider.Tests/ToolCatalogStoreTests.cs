using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;

namespace Madorin.AI.Runtime.Provider.Tests;

[TestClass]
public sealed class ToolCatalogStoreTests
{
    [TestMethod]
    public void ReplaceAndPatch_PreserveExistingInvocationSnapshot()
    {
        var store = CreateStore();
        var first = store.ReplaceHostCatalog(new ToolCatalogReplaceRequest(
            "host-v1",
            [CreateHostTool("host.search")]));

        var second = store.PatchHostCatalog(new ToolCatalogPatchRequest(
            "host-v1",
            "host-v2",
            [CreateHostTool("host.fetch")],
            ["host.search"]));

        Assert.AreEqual("host-v1", first.HostCatalogVersion);
        Assert.IsTrue(first.TryGetTool("host.search", out _));
        Assert.IsFalse(first.TryGetTool("host.fetch", out _));
        Assert.AreEqual("host-v2", second.HostCatalogVersion);
        Assert.IsFalse(second.TryGetTool("host.search", out _));
        Assert.IsTrue(second.TryGetTool("host.fetch", out _));
        Assert.AreNotEqual(first.EffectiveVersion, second.EffectiveVersion);
        Assert.IsTrue(first.TryGetTool(BuiltinToolRegistry.MemoryReadToolId, out _));
        Assert.IsTrue(second.TryGetTool(BuiltinToolRegistry.MemoryReadToolId, out _));
    }

    [TestMethod]
    public void Replace_RejectsReservedDuplicateAndConflictingVersions()
    {
        var store = CreateStore();
        var valid = new ToolCatalogReplaceRequest(
            "host-v1",
            [CreateHostTool("host.search")]);
        var first = store.ReplaceHostCatalog(valid);
        var repeated = store.ReplaceHostCatalog(valid);

        Assert.AreSame(first, repeated);
        Assert.ThrowsExactly<ArgumentException>(() =>
            store.ReplaceHostCatalog(new ToolCatalogReplaceRequest(
                "host-v2",
                [CreateHostTool("builtin.fs.escape")])));
        Assert.ThrowsExactly<ArgumentException>(() =>
            store.ReplaceHostCatalog(new ToolCatalogReplaceRequest(
                "host-v2",
                [CreateHostTool("host.same"), CreateHostTool("host.same")])));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            store.ReplaceHostCatalog(new ToolCatalogReplaceRequest(
                "host-v1",
                [CreateHostTool("host.other")])));
    }

    [TestMethod]
    public void Patch_RejectsStaleBaseAndOverlappingChanges()
    {
        var store = CreateStore();
        store.ReplaceHostCatalog(new ToolCatalogReplaceRequest(
            "host-v1",
            [CreateHostTool("host.search")]));

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            store.PatchHostCatalog(new ToolCatalogPatchRequest(
                "stale-v0",
                "host-v2",
                [],
                [])));
        Assert.ThrowsExactly<ArgumentException>(() =>
            store.PatchHostCatalog(new ToolCatalogPatchRequest(
                "host-v1",
                "host-v2",
                [CreateHostTool("host.search")],
                ["host.search"])));
    }

    [TestMethod]
    public void SchemaValidator_ValidatesDescriptorArgumentsAndResult()
    {
        var validator = new JsonSchemaToolValidator();
        var descriptor = new ToolDescriptor(
            "host.search",
            "host",
            "Search",
            "Searches data.",
            """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}""",
            """{"type":"object","properties":{"count":{"type":"integer"}},"required":["count"],"additionalProperties":false}""");

        Assert.IsTrue(validator.ValidateDescriptor(descriptor).IsValid);
        Assert.IsTrue(validator.ValidateArguments(
            descriptor,
            ParseElement("""{"query":"madorin"}""")).IsValid);
        Assert.IsFalse(validator.ValidateArguments(
            descriptor,
            ParseElement("""{"unexpected":true}""")).IsValid);
        Assert.IsTrue(validator.ValidateResult(
            descriptor,
            ParseElement("""{"count":1}""")).IsValid);
        Assert.IsFalse(validator.ValidateResult(
            descriptor,
            ParseElement("""{"count":"one"}""")).IsValid);
        Assert.IsFalse(validator.ValidateDescriptor(
            descriptor with { InputSchemaJson = "{not-json" }).IsValid);
    }

    private static ToolCatalogStore CreateStore() =>
        new(new BuiltinToolRegistry(), new JsonSchemaToolValidator());

    private static ToolCatalogItem CreateHostTool(string toolId)
    {
        var schema = ParseElement("""{"type":"object"}""");
        return new ToolCatalogItem(
            toolId,
            toolId,
            "Test tool",
            schema,
            schema,
            ToolRiskLevel.Low,
            30,
            [],
            [],
            ToolExecutionTarget.Host,
            RequiresApproval: false,
            IsIdempotent: true);
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
