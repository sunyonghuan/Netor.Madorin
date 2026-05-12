using System.Reflection;
using Cortana.Plugins.Memory.Mcp;
using Cortana.Plugins.Memory.ToolHandlers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Server;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;

namespace Memory.Test.Mcp;

/// <summary>
/// MCP 适配层测试：验证 <see cref="MemoryMcpTools"/> 仅做协议转发，
/// 不引入额外业务规则，并保留必要的 MCP 元数据。
/// </summary>
[TestClass]
public sealed class MemoryMcpToolsTests
{
    [TestMethod]
    public void Class_Should_Be_Marked_As_McpServerToolType()
    {
        var attribute = typeof(MemoryMcpTools).GetCustomAttribute<McpServerToolTypeAttribute>();
        Assert.IsNotNull(attribute, "MemoryMcpTools 必须使用 [McpServerToolType] 标记。");
    }

    [TestMethod]
    [DataRow(nameof(MemoryMcpTools.Recall), "memory_recall")]
    [DataRow(nameof(MemoryMcpTools.RecordTurn), "memory_record_turn")]
    [DataRow(nameof(MemoryMcpTools.SetScope), "memory_set_scope")]
    [DataRow(nameof(MemoryMcpTools.GetScope), "memory_get_scope")]
    [DataRow(nameof(MemoryMcpTools.SupplyContext), "memory_supply_context")]
    [DataRow(nameof(MemoryMcpTools.GetStatus), "memory_get_status")]
    [DataRow(nameof(MemoryMcpTools.AddNote), "memory_add_note")]
    [DataRow(nameof(MemoryMcpTools.ListRecent), "memory_list_recent")]
    public void Each_Tool_Should_Expose_Expected_McpServerTool_Name(string methodName, string expectedToolName)
    {
        var method = typeof(MemoryMcpTools).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        Assert.IsNotNull(method, $"未找到方法 {methodName}");

        var attribute = method!.GetCustomAttribute<McpServerToolAttribute>();
        Assert.IsNotNull(attribute, $"{methodName} 必须使用 [McpServerTool] 标记。");
        Assert.AreEqual(expectedToolName, attribute!.Name);

        var description = method.GetCustomAttribute<DescriptionAttribute>();
        Assert.IsNotNull(description, $"{methodName} 必须提供 [Description]。");
        Assert.IsFalse(string.IsNullOrWhiteSpace(description!.Description));
    }

    [TestMethod]
    public void Recall_Should_Forward_All_Arguments_To_ReadHandler()
    {
        var read = new RecordingReadHandler { RecallReturn = "recall-result" };
        var write = new RecordingWriteHandler();
        var mcp = new RecordingMcpHandler();
        var tools = new MemoryMcpTools(read, write, mcp);

        var result = tools.Recall("查询文本", "intent-x", "agent-1", "ws-1", 12);

        Assert.AreEqual("recall-result", result);
        Assert.AreEqual(1, read.RecallCalls);
        Assert.AreEqual(("查询文本", "intent-x", "agent-1", "ws-1", 12), read.LastRecallArgs);
    }

    [TestMethod]
    public void SupplyContext_Should_Forward_All_Arguments_To_ReadHandler()
    {
        var read = new RecordingReadHandler { SupplyReturn = "supply-result" };
        var write = new RecordingWriteHandler();
        var mcp = new RecordingMcpHandler();
        var tools = new MemoryMcpTools(read, write, mcp);

        var result = tools.SupplyContext(
            scenario: "chat",
            currentTask: "总结日报",
            recentMessages: "msg-1\nmsg-2",
            workspaceId: "ws-2",
            maxMemoryCount: 10,
            maxTokenBudget: 2048,
            triggerSource: "tool");

        Assert.AreEqual("supply-result", result);
        Assert.AreEqual(1, read.SupplyCalls);
        Assert.AreEqual(("chat", "总结日报", "msg-1\nmsg-2", "ws-2", 10, 2048, "tool"), read.LastSupplyArgs);
    }

    [TestMethod]
    public void GetStatus_Should_Forward_WorkspaceId_To_ReadHandler()
    {
        var read = new RecordingReadHandler { StatusReturn = "status-result" };
        var write = new RecordingWriteHandler();
        var mcp = new RecordingMcpHandler();
        var tools = new MemoryMcpTools(read, write, mcp);

        var result = tools.GetStatus("ws-3");

        Assert.AreEqual("status-result", result);
        Assert.AreEqual(1, read.StatusCalls);
        Assert.AreEqual("ws-3", read.LastStatusWorkspaceId);
    }

    [TestMethod]
    public void AddNote_Should_Forward_All_Arguments_To_WriteHandler()
    {
        var read = new RecordingReadHandler();
        var write = new RecordingWriteHandler { AddNoteReturn = "add-note-result" };
        var mcp = new RecordingMcpHandler();
        var tools = new MemoryMcpTools(read, write, mcp);

        var result = tools.AddNote(
            content: "记忆内容",
            reason: "用户授权",
            userConfirmed: true,
            memoryType: "preference",
            topic: "coding-style",
            workspaceId: "ws-4");

        Assert.AreEqual("add-note-result", result);
        Assert.AreEqual(1, write.AddNoteCalls);
        Assert.AreEqual(("记忆内容", "preference", "coding-style", "用户授权", "ws-4", true), write.LastAddNoteArgs);
    }

    [TestMethod]
    public void AddNote_Should_Forward_UserConfirmed_False_Without_Filtering()
    {
        var read = new RecordingReadHandler();
        var write = new RecordingWriteHandler { AddNoteReturn = "add-note-result" };
        var mcp = new RecordingMcpHandler();
        var tools = new MemoryMcpTools(read, write, mcp);

        // MCP 适配层不做规则判断，由 handler 决定是否拒绝。
        tools.AddNote(
            content: "x",
            reason: "y",
            userConfirmed: false);

        Assert.AreEqual(1, write.AddNoteCalls);
        Assert.IsFalse(write.LastAddNoteArgs.Item6);
    }

    [TestMethod]
    public void ListRecent_Should_Forward_All_Arguments_To_WriteHandler()
    {
        var read = new RecordingReadHandler();
        var write = new RecordingWriteHandler { ListRecentReturn = "list-recent-result" };
        var mcp = new RecordingMcpHandler();
        var tools = new MemoryMcpTools(read, write, mcp);

        var result = tools.ListRecent(limit: 7, kind: "fragment", workspaceId: "ws-5");

        Assert.AreEqual("list-recent-result", result);
        Assert.AreEqual(1, write.ListRecentCalls);
        Assert.AreEqual((7, "fragment", "ws-5"), write.LastListRecentArgs);
    }

    [TestMethod]
    public void RecordTurn_Should_Forward_All_Arguments_To_McpHandler()
    {
        var read = new RecordingReadHandler();
        var write = new RecordingWriteHandler();
        var mcp = new RecordingMcpHandler { RecordTurnReturn = "record-turn-result" };
        var tools = new MemoryMcpTools(read, write, mcp);

        var result = tools.RecordTurn(
            role: "user",
            content: "hello",
            agentId: "agent-1",
            workspaceId: "ws-6",
            sessionId: "session-1",
            turnId: "turn-1",
            messageId: "message-1",
            source: "cline",
            createdTimestamp: 123456);

        Assert.AreEqual("record-turn-result", result);
        Assert.AreEqual(1, mcp.RecordTurnCalls);
        Assert.AreEqual(("user", "hello", "agent-1", "ws-6", "session-1", "turn-1", "message-1", "cline", 123456), mcp.LastRecordTurnArgs);
    }

    [TestMethod]
    public void ScopeTools_Should_Forward_To_McpHandler()
    {
        var read = new RecordingReadHandler();
        var write = new RecordingWriteHandler();
        var mcp = new RecordingMcpHandler { SetScopeReturn = "set-scope-result", GetScopeReturn = "get-scope-result" };
        var tools = new MemoryMcpTools(read, write, mcp);

        var setResult = tools.SetScope("agent-x", "ws-x", "client-x");
        var getResult = tools.GetScope();

        Assert.AreEqual("set-scope-result", setResult);
        Assert.AreEqual("get-scope-result", getResult);
        Assert.AreEqual(1, mcp.SetScopeCalls);
        Assert.AreEqual(1, mcp.GetScopeCalls);
        Assert.AreEqual(("agent-x", "ws-x", "client-x"), mcp.LastSetScopeArgs);
    }

    [TestMethod]
    public void Optional_Parameters_Should_Default_To_Empty_Or_Zero()
    {
        var read = new RecordingReadHandler { RecallReturn = "ok", SupplyReturn = "ok", StatusReturn = "ok" };
        var write = new RecordingWriteHandler { AddNoteReturn = "ok", ListRecentReturn = "ok" };
        var mcp = new RecordingMcpHandler { RecordTurnReturn = "ok", SetScopeReturn = "ok", GetScopeReturn = "ok" };
        var tools = new MemoryMcpTools(read, write, mcp);

        // 仅传必填参数，验证默认值传递到 handler。
        tools.Recall("查询");
        Assert.AreEqual(("查询", "", "", "", 0), read.LastRecallArgs);

        tools.SupplyContext();
        Assert.AreEqual(("", "", "", "", 0, 0, ""), read.LastSupplyArgs);

        tools.GetStatus();
        Assert.AreEqual("", read.LastStatusWorkspaceId);

        tools.AddNote("内容", "原因", userConfirmed: true);
        Assert.AreEqual(("内容", "note", "", "原因", "", true), write.LastAddNoteArgs);

        tools.ListRecent();
        Assert.AreEqual((0, "", ""), write.LastListRecentArgs);

        tools.RecordTurn("user", "内容");
        Assert.AreEqual(("user", "内容", "", "", "", "", "", "", 0), mcp.LastRecordTurnArgs);

        tools.SetScope();
        Assert.AreEqual(("", "", ""), mcp.LastSetScopeArgs);
    }

    private sealed class RecordingReadHandler : IMemoryReadToolHandler
    {
        public int RecallCalls { get; private set; }
        public int SupplyCalls { get; private set; }
        public int StatusCalls { get; private set; }
        public (string, string, string, string, int) LastRecallArgs { get; private set; }
        public (string, string, string, string, int, int, string) LastSupplyArgs { get; private set; }
        public string LastStatusWorkspaceId { get; private set; } = string.Empty;
        public string RecallReturn { get; init; } = string.Empty;
        public string SupplyReturn { get; init; } = string.Empty;
        public string StatusReturn { get; init; } = string.Empty;

        public string Recall(string queryText, string queryIntent, string agentId, string workspaceId, int maxMemoryCount)
        {
            RecallCalls++;
            LastRecallArgs = (queryText, queryIntent, agentId, workspaceId, maxMemoryCount);
            return RecallReturn;
        }

        public string SupplyContext(string scenario, string currentTask, string recentMessages, string workspaceId, int maxMemoryCount, int maxTokenBudget, string triggerSource)
        {
            SupplyCalls++;
            LastSupplyArgs = (scenario, currentTask, recentMessages, workspaceId, maxMemoryCount, maxTokenBudget, triggerSource);
            return SupplyReturn;
        }

        public string GetStatus(string workspaceId)
        {
            StatusCalls++;
            LastStatusWorkspaceId = workspaceId;
            return StatusReturn;
        }
    }

    private sealed class RecordingWriteHandler : IMemoryWriteToolHandler
    {
        public int AddNoteCalls { get; private set; }
        public int ListRecentCalls { get; private set; }
        public (string, string, string, string, string, bool) LastAddNoteArgs { get; private set; }
        public (int, string, string) LastListRecentArgs { get; private set; }
        public string AddNoteReturn { get; init; } = string.Empty;
        public string ListRecentReturn { get; init; } = string.Empty;

        public string AddNote(string content, string memoryType, string topic, string reason, string workspaceId, bool userConfirmed)
        {
            AddNoteCalls++;
            LastAddNoteArgs = (content, memoryType, topic, reason, workspaceId, userConfirmed);
            return AddNoteReturn;
        }

        public string ListRecent(int limit, string kind, string workspaceId)
        {
            ListRecentCalls++;
            LastListRecentArgs = (limit, kind, workspaceId);
            return ListRecentReturn;
        }

        public string GetSettings(string workspaceId) => string.Empty;

        public string UpdateSetting(string settingKey, string settingValue, string reason, string workspaceId, bool userConfirmed) => string.Empty;

        public string DeleteMemory(string memoryId, string reason, bool userConfirmed) => string.Empty;

        public string TriggerProcessing(int maxCount) => string.Empty;
    }

    private sealed class RecordingMcpHandler : IMemoryMcpToolHandler
    {
        public int RecordTurnCalls { get; private set; }
        public int SetScopeCalls { get; private set; }
        public int GetScopeCalls { get; private set; }
        public (string, string, string, string, string, string, string, string, long) LastRecordTurnArgs { get; private set; }
        public (string, string, string) LastSetScopeArgs { get; private set; }
        public string RecordTurnReturn { get; init; } = string.Empty;
        public string SetScopeReturn { get; init; } = string.Empty;
        public string GetScopeReturn { get; init; } = string.Empty;

        public string RecordTurn(string role, string content, string agentId, string workspaceId, string sessionId, string turnId, string messageId, string source, long createdTimestamp)
        {
            RecordTurnCalls++;
            LastRecordTurnArgs = (role, content, agentId, workspaceId, sessionId, turnId, messageId, source, createdTimestamp);
            return RecordTurnReturn;
        }

        public string SetScope(string agentId, string workspaceId, string source)
        {
            SetScopeCalls++;
            LastSetScopeArgs = (agentId, workspaceId, source);
            return SetScopeReturn;
        }

        public string GetScope()
        {
            GetScopeCalls++;
            return GetScopeReturn;
        }
    }
}
