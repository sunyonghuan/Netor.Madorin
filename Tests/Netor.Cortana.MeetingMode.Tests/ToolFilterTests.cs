using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.AI;

namespace Netor.Cortana.MeetingMode.Tests;

[TestClass]
public sealed class ToolFilterTests
{
    [TestMethod]
    public void RegisterAdditionalToolNames_ReservesNamesForPluginAndMcpConflictChecks()
    {
        var attachmentTool = CreateTool("list_meeting_attachments", "列出会议附件清单。");
        var registeredTools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        AIAgentFactory.RegisterAdditionalToolNames([attachmentTool], registeredTools);

        Assert.IsTrue(registeredTools.ContainsKey("LIST_MEETING_ATTACHMENTS"));
        Assert.IsFalse(registeredTools.TryAdd("list_meeting_attachments", "插件 Duplicate(plugin-duplicate)"));
        Assert.AreEqual("额外工具 list_meeting_attachments", registeredTools["list_meeting_attachments"]);
    }

    [TestMethod]
    public void Apply_ReadOnly_FiltersMutableTools_AndKeepsReadOnlyTools()
    {
        var readTool = CreateTool("read_file", "读取文件内容");
        var writeTool = CreateTool("write_file", "读取并写入文件");
        var sendTool = CreateTool("notify_user", "发送通知给用户");
        var approvalTool = CreateTool(
            "list_secrets",
            "列出密钥",
            new AdditionalPropertiesDictionary(new Dictionary<string, object?>
            {
                ["ApprovalRequired"] = true,
            }));

        var filtered = ToolFilter.Apply(
            [readTool, writeTool, sendTool, approvalTool],
            ToolFilterMode.ReadOnly);

        Assert.HasCount(1, filtered);
        Assert.AreSame(readTool, filtered[0]);
    }

    [TestMethod]
    public void Apply_Full_DoesNotFilterTools()
    {
        var readTool = CreateTool("read_file", "读取文件内容");
        var writeTool = CreateTool("write_file", "写入文件内容");

        var filtered = ToolFilter.Apply([readTool, writeTool], ToolFilterMode.Full);

        Assert.HasCount(2, filtered);
        Assert.AreSame(readTool, filtered[0]);
        Assert.AreSame(writeTool, filtered[1]);
    }

    [TestMethod]
    public void Apply_None_RemovesAllTools()
    {
        var readTool = CreateTool("read_file", "读取文件内容");
        var summaryTool = CreateTool("output_summary", "输出会议总结。");

        var filtered = ToolFilter.Apply([readTool, summaryTool], ToolFilterMode.None);

        Assert.HasCount(0, filtered);
    }

    [TestMethod]
    public void Apply_ReadOnly_KeepsMeetingControlTools()
    {
        var tools = new[]
        {
            CreateTool("ask_user", "向用户提出一个必须由用户决策或补充的问题。"),
            CreateTool("output_summary", "输出会议总结。"),
            CreateTool("check_pending_user_input", "检查用户插话队列，在调用 ask_user 前必须先调用。"),
            CreateTool("confirm_meeting_end", "标记会议正式结束。"),
            CreateTool("list_meeting_attachments", "列出会议附件清单。"),
            CreateTool("start_work_task", "当用户明确要求开始执行已讨论清楚的方案时，创建工作模式任务并切换到工作模式。")
        };

        var filtered = ToolFilter.Apply(tools, ToolFilterMode.ReadOnly);

        Assert.HasCount(6, filtered);
        CollectionAssert.AreEqual(
            tools.Select(t => t.Name).ToArray(),
            filtered.Select(t => t.Name).ToArray());
    }

    [TestMethod]
    public void Apply_MeetingHostExecution_KeepsMeetingFileAndPowerShellToolsOnly()
    {
        var tools = new[]
        {
            CreateTool("ask_user", "向用户提出一个必须由用户决策或补充的问题。"),
            CreateTool("output_summary", "输出会议总结。"),
            CreateTool("list_meeting_attachments", "列出会议附件清单。"),
            CreateTool("sys_read_file", "读取文件内容。"),
            CreateTool("sys_write_file", "写入文件。"),
            CreateTool("sys_write_large_file", "写入大文件。"),
            CreateTool("sys_execute_powershell", "执行 PowerShell。"),
            CreateTool("sys_send_command", "发送 PowerShell 会话命令。"),
            CreateTool("sys_get_workspace_directory", "获取工作区目录。"),
            CreateTool("sys_set_default_model", "设置默认模型。"),
            CreateTool("sys_show_main_window", "显示主窗口。"),
            CreateTool("sys_reload_plugin", "重载插件。"),
            CreateTool("dispatch_step", "派发工作模式步骤。"),
            CreateTool("list_system_tool_catalog", "列出后台子智能体可挂载的系统工具目录。"),
            CreateTool("start_autonomous_subagent_task", "创建临时后台子智能体任务。"),
            CreateTool("get_subagent_task_status", "查询后台子智能体任务状态。"),
            CreateTool("read_subagent_task_result", "读取后台子智能体任务结果。"),
            CreateTool("cancel_subagent_task", "取消后台子智能体任务。")
        };

        var filtered = ToolFilter.Apply(tools, ToolFilterMode.MeetingHostExecution);

        CollectionAssert.AreEqual(
            new[]
            {
                "ask_user",
                "output_summary",
                "list_meeting_attachments",
                "sys_read_file",
                "sys_write_file",
                "sys_write_large_file",
                "sys_execute_powershell",
                "sys_send_command",
                "sys_get_workspace_directory"
            },
            filtered.Select(t => t.Name).ToArray());
    }

    [TestMethod]
    public void Apply_WorkModeManager_KeepsManagerToolsAndReadOnlySystemToolsOnly()
    {
        var tools = new[]
        {
            CreateTool("set_plan", "制定工作计划。"),
            CreateTool("finalize_plan", "交付计划。"),
            CreateTool("update_plan", "更新计划。"),
            CreateTool("cancel_task", "取消任务。"),
            CreateTool("sys_read_file", "读取文件。"),
            CreateTool("sys_search_files", "搜索文件。"),
            CreateTool("dispatch_step", "派发步骤。"),
            CreateTool("verify_step", "验证步骤。"),
            CreateTool("start_subagent_background", "启动子智能体。"),
            CreateTool("agent_writer", "直接调用写作专员。"),
            CreateTool("sys_write_file", "写入文件。"),
            CreateTool("sys_set_window_topmost", "设置窗口置顶。"),
            CreateTool("list_system_tool_catalog", "列出后台子智能体可挂载的系统工具目录。"),
            CreateTool("start_autonomous_subagent_task", "创建临时后台子智能体任务。"),
            CreateTool("get_subagent_task_status", "查询后台子智能体任务状态。"),
            CreateTool("read_subagent_task_result", "读取后台子智能体任务结果。"),
            CreateTool("cancel_subagent_task", "取消后台子智能体任务。")
        };

        var filtered = ToolFilter.Apply(tools, ToolFilterMode.WorkModeManager);

        CollectionAssert.AreEqual(
            new[]
            {
                "set_plan",
                "finalize_plan",
                "update_plan",
                "cancel_task",
                "sys_read_file",
                "sys_search_files",
                "sys_write_file"
            },
            filtered.Select(t => t.Name).ToArray());
    }

    [TestMethod]
    public void Apply_NonExpertModes_DoNotExposeExpertDelegationTools()
    {
        var delegationTools = new[]
        {
            CreateTool("list_system_tool_catalog", "列出后台子智能体可挂载的系统工具目录。"),
            CreateTool("start_autonomous_subagent_task", "创建临时后台子智能体任务。"),
            CreateTool("get_subagent_task_status", "查询后台子智能体任务状态。"),
            CreateTool("read_subagent_task_result", "读取后台子智能体任务结果。"),
            CreateTool("cancel_subagent_task", "取消后台子智能体任务。")
        };

        var workModeFiltered = ToolFilter.Apply(delegationTools, ToolFilterMode.WorkModeManager);
        var meetingHostFiltered = ToolFilter.Apply(delegationTools, ToolFilterMode.MeetingHostExecution);
        var readOnlyFiltered = ToolFilter.Apply(delegationTools, ToolFilterMode.ReadOnly);

        Assert.IsEmpty(workModeFiltered);
        Assert.IsEmpty(meetingHostFiltered);
        Assert.IsEmpty(readOnlyFiltered);
    }

    private static AIFunction CreateTool(
        string name,
        string description,
        AdditionalPropertiesDictionary? additionalProperties = null)
    {
        Task<string> InvokeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("ok");
        }

        return AIFunctionFactory.Create(InvokeAsync, new AIFunctionFactoryOptions
        {
            Name = name,
            Description = description,
            AdditionalProperties = additionalProperties,
        });
    }
}
