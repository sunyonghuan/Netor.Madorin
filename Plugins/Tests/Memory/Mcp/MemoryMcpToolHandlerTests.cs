using System.Text.Json;
using Cortana.Plugins.Memory.Mcp;
using Cortana.Plugins.Memory.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Memory.Test.Storage;

namespace Memory.Test.Mcp;

[TestClass]
public sealed class MemoryMcpToolHandlerTests
{
    [TestMethod]
    public void RecordTurn_Should_Write_Observation_With_Current_Scope()
    {
        using var fixture = new MemoryStorageTestFixture();
        var runtimeContext = new MemoryRuntimeContext("mcp-default", "default", "mcp");
        var writer = new MemoryObservationWriter(fixture.Store, runtimeContext);
        var handler = new MemoryMcpToolHandler(writer, runtimeContext, NullLogger<MemoryMcpToolHandler>.Instance);

        handler.SetScope("agent-test", "workspace-test", "cline");
        var json = handler.RecordTurn(
            role: "user",
            content: "请记住这个偏好。",
            agentId: string.Empty,
            workspaceId: string.Empty,
            sessionId: "session-test",
            turnId: "turn-test",
            messageId: "message-test",
            source: string.Empty,
            createdTimestamp: 123456789);

        using var document = JsonDocument.Parse(json);
        Assert.IsTrue(document.RootElement.GetProperty("success").GetBoolean());
        var data = document.RootElement.GetProperty("data").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(data));

        using var dataDocument = JsonDocument.Parse(data!);
        var observationId = dataDocument.RootElement.GetProperty("ObservationId").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(observationId));

        var record = fixture.ObservationRecords.GetById(observationId!);
        Assert.IsNotNull(record);
        Assert.AreEqual("agent-test", record!.AgentId);
        Assert.AreEqual("workspace-test", record.WorkspaceId);
        Assert.AreEqual("session-test", record.SessionId);
        Assert.AreEqual("turn-test", record.TurnId);
        Assert.AreEqual("message-test", record.MessageId);
        Assert.AreEqual("user", record.Role);
        Assert.AreEqual("请记住这个偏好。", record.Content);
        Assert.AreEqual("mcp.message.recorded", record.EventType);
    }
}
