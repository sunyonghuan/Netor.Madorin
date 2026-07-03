using Microsoft.Extensions.AI;

namespace Netor.Cortana.AI;

/// <summary>
/// 智能体工具过滤模式。
/// </summary>
public enum ToolFilterMode
{
    /// <summary>不过滤工具，保持完整能力。</summary>
    Full,

    /// <summary>仅保留查询、读取类工具，用于会议模式。</summary>
    ReadOnly,

    /// <summary>会议主持人可用的会议控制、文件读写和 PowerShell 执行工具。</summary>
    MeetingHostExecution,

    /// <summary>仅保留工作模式总经理需要的规划、编排控制、对话和只读系统工具。</summary>
    WorkModeManager,

    /// <summary>不暴露任何工具，用于只需要纯文本/JSON 决策的内部智能体。</summary>
    None,
}

/// <summary>
/// 智能体工具只读判定规则。
/// </summary>
public static class ToolFilter
{
    private static readonly HashSet<string> ExpertDelegationToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "list_system_tool_catalog",
        "start_autonomous_subagent_task",
        "get_subagent_task_status",
        "read_subagent_task_result",
        "cancel_subagent_task"
    };

    private static readonly HashSet<string> MeetingControlToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ask_user",
        "output_summary",
        "check_pending_user_input",
        "confirm_meeting_end",
        "list_meeting_attachments",
        "start_work_task"
    };

    private static readonly HashSet<string> WorkModeManagerToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "set_plan",
        "get_plan",
        "update_plan",
        "set_environment",
        "finalize_plan",
        "pause_orchestrator",
        "resume_orchestrator",
        "cancel_orchestrator",
        "list_plan_templates",
        "load_plan_from_template",
        "save_current_plan_as_template",
        "load_plan_from_chat_history",
        "load_plan_from_groupchat",
        "get_recent_completed_task_plan",
        "get_execution_logs",
        "ask_user",
        "check_pending_user_input",
        "final_report",
        "cancel_task",
        "close_task_record",
        "sys_list_directory",
        "sys_get_file_info",
        "sys_read_file",
        "sys_search_files",
        "sys_create_file",
        "sys_write_file",
        "sys_write_large_file",
        "sys_write_files_batch",
        "sys_edit_file",
        "sys_create_directory"
    };

    private static readonly HashSet<string> MeetingHostExecutionToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ask_user",
        "output_summary",
        "check_pending_user_input",
        "confirm_meeting_end",
        "list_meeting_attachments",
        "start_work_task",
        "sys_list_directory",
        "sys_get_file_info",
        "sys_read_file",
        "sys_search_files",
        "sys_create_file",
        "sys_write_file",
        "sys_write_large_file",
        "sys_write_files_batch",
        "sys_edit_file",
        "sys_delete_file",
        "sys_move_file",
        "sys_create_directory",
        "sys_delete_directory",
        "sys_execute_powershell",
        "sys_start_local_session",
        "sys_get_session_status",
        "sys_send_command",
        "sys_close_session",
        "sys_list_sessions",
        "sys_get_workspace_directory",
        "sys_get_user_data_directory",
        "sys_get_workspaceId"
    };

    private static readonly string[] MutableNamePrefixes =
    [
        "write_", "create_", "update_", "delete_", "remove_", "edit_", "save_",
        "send_", "post_", "publish_", "deploy_", "execute_", "run_", "invoke_",
        "cancel_", "clear_", "set_", "start_", "stop_", "restart_", "move_",
        "copy_", "mkdir_", "touch_", "shell_", "powershell_", "command_"
    ];

    private static readonly string[] MutableDescriptionWords =
    [
        "write", "create", "update", "delete", "remove", "edit", "save",
        "send", "post", "publish", "deploy", "execute", "run", "invoke",
        "cancel", "clear", "modify", "append", "overwrite", "upload",
        "download", "写入", "创建", "新增", "更新", "删除", "移除",
        "修改", "保存", "发送", "发布", "部署", "执行", "取消", "清除",
        "追加", "覆盖", "上传", "下载"
    ];

    /// <summary>根据模式过滤工具列表。</summary>
    public static IReadOnlyList<AITool> Apply(IEnumerable<AITool>? tools, ToolFilterMode mode)
    {
        if (tools is null)
        {
            return [];
        }

        if (mode == ToolFilterMode.Full)
        {
            return tools as IReadOnlyList<AITool> ?? tools.ToList();
        }

        if (mode == ToolFilterMode.None)
        {
            return [];
        }

        if (mode == ToolFilterMode.WorkModeManager)
        {
            var workModeTools = new List<AITool>();
            foreach (var tool in tools)
            {
                if (IsWorkModeManagerTool(tool))
                {
                    workModeTools.Add(tool);
                }
            }

            return workModeTools;
        }

        if (mode == ToolFilterMode.MeetingHostExecution)
        {
            var hostTools = new List<AITool>();
            foreach (var tool in tools)
            {
                if (IsMeetingHostExecutionTool(tool))
                {
                    hostTools.Add(tool);
                }
            }

            return hostTools;
        }

        var filtered = new List<AITool>();
        foreach (var tool in tools)
        {
            if (IsReadOnlyTool(tool))
            {
                filtered.Add(tool);
            }
        }

        return filtered;
    }

    /// <summary>判断工具是否可在只读模式下暴露给智能体。</summary>
    public static bool IsReadOnlyTool(AITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (IsExpertDelegationTool(tool))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(tool.Name) && MeetingControlToolNames.Contains(tool.Name))
        {
            return true;
        }

        return !RequiresApproval(tool)
            && !HasMutableName(tool.Name)
            && !HasMutableDescription(tool.Description);
    }

    private static bool IsWorkModeManagerTool(AITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var name = tool.Name;
        return !string.IsNullOrWhiteSpace(name)
            && WorkModeManagerToolNames.Contains(name);
    }

    private static bool IsMeetingHostExecutionTool(AITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return !string.IsNullOrWhiteSpace(tool.Name)
            && MeetingHostExecutionToolNames.Contains(tool.Name);
    }

    private static bool IsExpertDelegationTool(AITool tool)
    {
        return !string.IsNullOrWhiteSpace(tool.Name)
            && ExpertDelegationToolNames.Contains(tool.Name);
    }

    private static bool RequiresApproval(AITool tool)
    {
        if (tool is ApprovalRequiredAIFunction)
        {
            return true;
        }

        var typeName = tool.GetType().Name;
        if (typeName.Contains("ApprovalRequired", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (tool.AdditionalProperties is null)
        {
            return false;
        }

        return TryReadBool(tool.AdditionalProperties, "ApprovalRequired")
            || TryReadBool(tool.AdditionalProperties, "approval_required")
            || TryReadBool(tool.AdditionalProperties, "RequiresApproval")
            || TryReadBool(tool.AdditionalProperties, "requires_approval");
    }

    private static bool TryReadBool(IReadOnlyDictionary<string, object?> properties, string key)
    {
        if (!properties.TryGetValue(key, out var value))
        {
            return false;
        }

        return value switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var b) && b,
            _ => false,
        };
    }

    private static bool HasMutableName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        foreach (var prefix in MutableNamePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasMutableDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return false;
        }

        foreach (var word in MutableDescriptionWords)
        {
            if (description.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
