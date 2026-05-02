using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Memory.Test.Storage;
using Netor.Cortana.Plugin;
using System.Text.Json;

namespace Memory.Test.Services;

[TestClass]
public sealed class MemoryP1ServiceTests
{
    [TestMethod]
    public void IngestLiveEvent_Should_Buffer_Assistant_Deltas_Until_Turn_Completed()
    {
        using var fixture = new MemoryStorageTestFixture();
        var service = CreateIngestService(fixture);

        service.TryIngestLiveEvent("conversation.assistant.delta", ParsePayload("""
            {
              "sessionId": "session-stream-test",
              "turnId": "turn-stream-test",
              "assistantMessageId": "assistant-stream-test",
              "agentId": "agent-test",
              "workspaceId": "workspace-test",
              "delta": "你好，",
              "sequence": 1,
              "occurredAt": 1000
            }
            """));
        service.TryIngestLiveEvent("conversation.assistant.delta", ParsePayload("""
            {
              "sessionId": "session-stream-test",
              "turnId": "turn-stream-test",
              "assistantMessageId": "assistant-stream-test",
              "agentId": "agent-test",
              "workspaceId": "workspace-test",
              "delta": "这是完整回复。",
              "sequence": 2,
              "occurredAt": 1100
            }
            """));
        service.TryIngestLiveEvent("conversation.turn.completed", ParsePayload("""
            {
              "sessionId": "session-stream-test",
              "turnId": "turn-stream-test",
              "assistantMessageId": "assistant-stream-test",
              "agentId": "agent-test",
              "workspaceId": "workspace-test",
              "status": "succeeded",
              "occurredAt": 1200
            }
            """));

        var observations = fixture.ObservationRecords.List(MemoryTestData.AgentId, MemoryTestData.WorkspaceId, 10);

        Assert.AreEqual(1, observations.Count);
        Assert.AreEqual("assistant-stream-test", observations[0].Id);
        Assert.AreEqual("assistant", observations[0].Role);
        Assert.AreEqual("你好，这是完整回复。", observations[0].Content);
        Assert.AreEqual("conversation.turn.completed", observations[0].EventType);
    }

    [TestMethod]
    public void IngestLiveEvent_Should_Not_Save_Failed_Assistant_Stream()
    {
        using var fixture = new MemoryStorageTestFixture();
        var service = CreateIngestService(fixture);

        service.TryIngestLiveEvent("conversation.assistant.delta", ParsePayload("""
            {
              "sessionId": "session-stream-test",
              "turnId": "turn-stream-test",
              "assistantMessageId": "assistant-failed-test",
              "agentId": "agent-test",
              "workspaceId": "workspace-test",
              "delta": "半截输出",
              "occurredAt": 1000
            }
            """));
        service.TryIngestLiveEvent("conversation.turn.completed", ParsePayload("""
            {
              "sessionId": "session-stream-test",
              "turnId": "turn-stream-test",
              "assistantMessageId": "assistant-failed-test",
              "agentId": "agent-test",
              "workspaceId": "workspace-test",
              "status": "failed",
              "occurredAt": 1200
            }
            """));

        var observations = fixture.ObservationRecords.List(MemoryTestData.AgentId, MemoryTestData.WorkspaceId, 10);

        Assert.AreEqual(0, observations.Count);
    }

    [TestMethod]
    public void AddNote_Should_Write_Candidate_Fragment_And_Mutation()
    {
        using var fixture = new MemoryStorageTestFixture();
        var service = new MemoryNoteService(fixture.Store);

        var result = service.AddNote(new MemoryAddNoteRequest
        {
            AgentId = MemoryTestData.AgentId,
            WorkspaceId = MemoryTestData.WorkspaceId,
            Content = "用户希望项目内所有注释使用中文。",
            MemoryType = "preference",
            Topic = "coding-style",
            Reason = "用户明确要求记住编码偏好。",
            UserConfirmed = true,
            TraceId = "trace-add-note-test"
        });

        var fragment = fixture.MemoryFragments.GetById(result.MemoryId);
        var mutation = fixture.MemoryMutations.GetById(result.MutationId);
        Assert.IsNotNull(fragment);
        Assert.AreEqual("candidate", fragment.LifecycleState);
        Assert.AreEqual("pending", fragment.ConfirmationState);
        Assert.IsNotNull(mutation);
        Assert.AreEqual(result.MemoryId, mutation.MemoryId);
        Assert.AreEqual("manual_create", mutation.MutationType);
    }

    [TestMethod]
    public void AddNote_Should_Require_User_Confirmation()
    {
        using var fixture = new MemoryStorageTestFixture();
        var service = new MemoryNoteService(fixture.Store);

        Assert.ThrowsExactly<InvalidOperationException>(() => service.AddNote(new MemoryAddNoteRequest
        {
            AgentId = MemoryTestData.AgentId,
            Content = "需要写入的记忆。",
            MemoryType = "fact",
            Reason = "测试未授权拒绝。",
            UserConfirmed = false
        }));
    }

    [TestMethod]
    public void AddNote_Should_Reject_Unsupported_Memory_Type()
    {
        using var fixture = new MemoryStorageTestFixture();
        var service = new MemoryNoteService(fixture.Store);

        Assert.ThrowsExactly<ArgumentException>(() => service.AddNote(new MemoryAddNoteRequest
        {
            AgentId = MemoryTestData.AgentId,
            Content = "需要写入的记忆。",
            MemoryType = "unsafe",
            Reason = "测试非法类型。",
            UserConfirmed = true
        }));
    }

    [TestMethod]
    public void ListRecent_Should_Return_Filtered_Recent_Memories()
    {
        using var fixture = new MemoryStorageTestFixture();
        var older = MemoryTestData.Fragment("recent-fragment-old");
        older.UpdatedAt = MemoryTestData.ToTime(1000);
        var newer = MemoryTestData.Abstraction("recent-abstraction-new");
        newer.UpdatedAt = MemoryTestData.ToTime(2000);
        fixture.Store.UpsertMemoryFragment(older);
        fixture.Store.UpsertMemoryAbstraction(newer);
        var service = new MemoryRecentService(fixture.Store);

        var result = service.ListRecent(new MemoryListRecentRequest
        {
            AgentId = MemoryTestData.AgentId,
            WorkspaceId = MemoryTestData.WorkspaceId,
            Limit = 10
        });

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("recent-abstraction-new", result.Items[0].Id);
        Assert.AreEqual("recent-fragment-old", result.Items[1].Id);
    }

    [TestMethod]
    public void ListRecent_Should_Filter_By_Kind()
    {
        using var fixture = new MemoryStorageTestFixture();
        fixture.Store.UpsertMemoryFragment(MemoryTestData.Fragment("recent-fragment-kind"));
        fixture.Store.UpsertMemoryAbstraction(MemoryTestData.Abstraction("recent-abstraction-kind"));
        var service = new MemoryRecentService(fixture.Store);

        var result = service.ListRecent(new MemoryListRecentRequest
        {
            AgentId = MemoryTestData.AgentId,
            WorkspaceId = MemoryTestData.WorkspaceId,
            Limit = 10,
            Kind = "fragment"
        });

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("fragment", result.Items[0].Kind);
    }

    [TestMethod]
    public void ListRecent_Should_Reject_Unsupported_Kind()
    {
        using var fixture = new MemoryStorageTestFixture();
        var service = new MemoryRecentService(fixture.Store);

        Assert.ThrowsExactly<ArgumentException>(() => service.ListRecent(new MemoryListRecentRequest
        {
            AgentId = MemoryTestData.AgentId,
            Kind = "unknown"
        }));
    }

    private static MemoryIngestService CreateIngestService(MemoryStorageTestFixture fixture)
    {
        var settings = new PluginSettings(
            Path.GetTempPath(),
            MemoryTestData.WorkspaceId,
            AppContext.BaseDirectory,
            0,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            0);

        return new MemoryIngestService(settings, NullLogger<MemoryIngestService>.Instance, fixture.Store);
    }

    private static JsonElement ParsePayload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
