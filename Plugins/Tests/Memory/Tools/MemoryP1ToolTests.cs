using System.Text.Json;
using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Services;
using Cortana.Plugins.Memory.ToolHandlers;
using Cortana.Plugins.Memory.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Memory.Test.Storage;

namespace Memory.Test.Tools;

[TestClass]
public sealed class MemoryP1ToolTests
{
    [TestMethod]
    public void AddNote_Should_Reject_When_User_Not_Confirmed()
    {
        var noteService = new FakeMemoryNoteService();
        var recentService = new FakeMemoryRecentService();
        var handler = new MemoryWriteToolHandler(noteService, recentService, NullLogger<MemoryWriteToolHandler>.Instance);
        var tools = new MemoryWriteTools(handler);

        var json = tools.AddNote(
            "用户希望记住中文注释偏好。",
            "preference",
            "coding-style",
            "用户明确要求记住。",
            MemoryTestData.WorkspaceId,
            userConfirmed: false);

        using var document = JsonDocument.Parse(json);
        Assert.IsFalse(document.RootElement.GetProperty("success").GetBoolean());
        Assert.AreEqual("INVALID_ARGUMENT", document.RootElement.GetProperty("code").GetString());
        Assert.IsFalse(noteService.WasCalled);
    }

    [TestMethod]
    public void AddNote_Should_Invoke_Service_And_Return_Result()
    {
        var noteService = new FakeMemoryNoteService();
        var recentService = new FakeMemoryRecentService();
        var handler = new MemoryWriteToolHandler(noteService, recentService, NullLogger<MemoryWriteToolHandler>.Instance);
        var tools = new MemoryWriteTools(handler);

        var json = tools.AddNote(
            "用户希望记住中文注释偏好。",
            "preference",
            "coding-style",
            "用户明确要求记住。",
            MemoryTestData.WorkspaceId,
            userConfirmed: true);

        using var document = JsonDocument.Parse(json);
        Assert.IsTrue(document.RootElement.GetProperty("success").GetBoolean());
        Assert.AreEqual("OK", document.RootElement.GetProperty("code").GetString());
        Assert.IsTrue(noteService.WasCalled);
        Assert.IsNotNull(noteService.LastRequest);
        Assert.AreEqual("preference", noteService.LastRequest.MemoryType);
        Assert.IsTrue(noteService.LastRequest.UserConfirmed);

        using var dataDocument = JsonDocument.Parse(document.RootElement.GetProperty("data").GetString()!);
        Assert.AreEqual("manual.test", dataDocument.RootElement.GetProperty("MemoryId").GetString());
        Assert.AreEqual("pending", dataDocument.RootElement.GetProperty("ConfirmationState").GetString());
    }

    [TestMethod]
    public void ListRecent_Should_Invoke_Service_And_Return_Items()
    {
        var noteService = new FakeMemoryNoteService();
        var recentService = new FakeMemoryRecentService();
        var handler = new MemoryWriteToolHandler(noteService, recentService, NullLogger<MemoryWriteToolHandler>.Instance);
        var tools = new MemoryWriteTools(handler);

        var json = tools.ListRecent(5, "fragment", MemoryTestData.WorkspaceId);

        using var document = JsonDocument.Parse(json);
        Assert.IsTrue(document.RootElement.GetProperty("success").GetBoolean());
        Assert.AreEqual("OK", document.RootElement.GetProperty("code").GetString());
        Assert.IsTrue(recentService.WasCalled);
        Assert.IsNotNull(recentService.LastRequest);
        Assert.AreEqual(5, recentService.LastRequest.Limit);
        Assert.AreEqual("fragment", recentService.LastRequest.Kind);

        using var dataDocument = JsonDocument.Parse(document.RootElement.GetProperty("data").GetString()!);
        Assert.AreEqual(1, dataDocument.RootElement.GetProperty("Count").GetInt32());
        Assert.AreEqual("recent.test", dataDocument.RootElement.GetProperty("Items")[0].GetProperty("Id").GetString());
    }

    private sealed class FakeMemoryNoteService : IMemoryNoteService
    {
        public bool WasCalled { get; private set; }
        public MemoryAddNoteRequest? LastRequest { get; private set; }

        public MemoryAddNoteResult AddNote(MemoryAddNoteRequest request)
        {
            WasCalled = true;
            LastRequest = request;
            return new MemoryAddNoteResult
            {
                MemoryId = "manual.test",
                Kind = "fragment",
                MemoryType = request.MemoryType,
                Topic = request.Topic ?? request.MemoryType,
                LifecycleState = "candidate",
                ConfirmationState = "pending",
                MutationId = "mutation.test",
                CreatedAt = MemoryTestData.ToTime(1000),
                Summary = "人工记忆已写入候选区，等待后续确认。"
            };
        }
    }

    private sealed class FakeMemoryRecentService : IMemoryRecentService
    {
        public bool WasCalled { get; private set; }
        public MemoryListRecentRequest? LastRequest { get; private set; }

        public MemoryListRecentResult ListRecent(MemoryListRecentRequest request)
        {
            WasCalled = true;
            LastRequest = request;
            return new MemoryListRecentResult
            {
                Count = 1,
                Limit = request.Limit ?? 20,
                Kind = request.Kind,
                Items =
                [
                    new MemoryRecentItem
                    {
                        Id = "recent.test",
                        Kind = request.Kind ?? "fragment",
                        Topic = "coding-style",
                        Title = "编码偏好",
                        Summary = "用户希望注释使用中文。",
                        Confidence = 0.8,
                        LifecycleState = "candidate",
                        ConfirmationState = "pending",
                        UpdatedAt = MemoryTestData.ToTime(1000)
                    }
                ]
            };
        }
    }
}
