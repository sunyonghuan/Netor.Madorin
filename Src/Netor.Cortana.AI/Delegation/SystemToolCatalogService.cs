using System.Text.Json;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Madorin.Plugin;

namespace Netor.Cortana.AI.Delegation;

public sealed record SystemToolCatalogItem(
    string ToolName,
    string Source,
    string Category,
    string Description,
    string Capability,
    string RiskLevel,
    string ParameterSummary);

/// <summary>
/// 汇总可供专家后台子智能体挂载的系统工具目录。
/// </summary>
public sealed class SystemToolCatalogService(
    IEnumerable<AIContextProvider> builtInProviders,
    PluginLoader pluginLoader)
{
    private static readonly string[] ParameterSectionMarkers =
    [
        "Parameters:",
        "参数：",
        "参数（",
        "参数("
    ];

    public async Task<IReadOnlyList<SystemToolCatalogItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;

        var items = new List<SystemToolCatalogItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in builtInProviders)
        {
            AddKnownBuiltInProviderTools(items, seen, provider.GetType().Name);
        }

        foreach (var pluginInfo in pluginLoader.GetLoadedPluginInfos())
        {
            foreach (var tool in pluginInfo.Plugin.Tools)
            {
                AddTool(items, seen, tool, $"plugin:{pluginInfo.Plugin.Id}", "plugin");
            }
        }

        foreach (var mcpHost in pluginLoader.GetActiveMcpServers())
        {
            foreach (var tool in mcpHost.Tools)
            {
                AddTool(items, seen, tool, $"mcp:{mcpHost.Id}", "mcp");
            }
        }

        return items
            .OrderBy(static item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.ToolName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> ValidateToolMountsAsync(
        IEnumerable<string>? toolMounts,
        CancellationToken cancellationToken = default)
    {
        var requested = NormalizeToolNames(toolMounts);
        if (requested.Count == 0)
        {
            return [];
        }

        var catalog = await ListAsync(cancellationToken).ConfigureAwait(false);
        var available = catalog
            .Select(static item => item.ToolName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return requested
            .Where(tool => !available.Contains(tool))
            .ToList();
    }

    public static IReadOnlyList<string> NormalizeToolNames(IEnumerable<string>? toolNames)
    {
        if (toolNames is null)
        {
            return [];
        }

        return toolNames
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddKnownBuiltInProviderTools(
        List<SystemToolCatalogItem> items,
        HashSet<string> seen,
        string source)
    {
        if (string.Equals(source, "FileBrowserProvider", StringComparison.Ordinal))
        {
            AddToolItem(items, seen, "sys_list_directory", source, "file", "List files and folders in the current workspace directory.");
            AddToolItem(items, seen, "sys_get_file_info", source, "file", "Get metadata for a file or folder in the current workspace directory.");
            AddToolItem(items, seen, "sys_read_file", source, "file", "Read a text or code file in the current workspace directory.");
            AddToolItem(items, seen, "sys_search_files", source, "file", "Search files in the current workspace directory by pattern.");
            AddToolItem(items, seen, "sys_get_drives", source, "file", "Get available system drives.");
            AddToolItem(items, seen, "sys_open_in_explorer", source, "file", "Open or reveal a file or folder in Explorer.");
            return;
        }

        if (string.Equals(source, "FileOperationProvider", StringComparison.Ordinal))
        {
            AddToolItem(items, seen, "sys_create_file", source, "file", "Create a new file in the current workspace directory.");
            AddToolItem(items, seen, "sys_write_file", source, "file", "Write a file in the current workspace directory.");
            AddToolItem(items, seen, "sys_write_large_file", source, "file", "Write a large file in the current workspace directory.");
            AddToolItem(items, seen, "sys_write_files_batch", source, "file", "Write multiple files in a single call.");
            AddToolItem(items, seen, "sys_edit_file", source, "file", "Edit an existing text file by line numbers.");
            AddToolItem(items, seen, "sys_delete_file", source, "file", "Delete a file in the current workspace directory.");
            AddToolItem(items, seen, "sys_move_file", source, "file", "Move or rename a file within the current workspace directory.");
            AddToolItem(items, seen, "sys_create_directory", source, "file", "Create a directory in the current workspace directory.");
            AddToolItem(items, seen, "sys_delete_directory", source, "file", "Delete an empty directory in the current workspace directory.");
            return;
        }

        if (string.Equals(source, "PowerShellProvider", StringComparison.Ordinal))
        {
            AddToolItem(items, seen, "sys_execute_powershell", source, "powershell", "Execute a local PowerShell script.");
            AddToolItem(items, seen, "sys_start_local_session", source, "powershell", "Start a local PowerShell interactive session.");
            AddToolItem(items, seen, "sys_get_session_status", source, "powershell", "Query a PowerShell session state.");
            AddToolItem(items, seen, "sys_send_command", source, "powershell", "Send a command to an active PowerShell session.");
            AddToolItem(items, seen, "sys_close_session", source, "powershell", "Close an active PowerShell session.");
            AddToolItem(items, seen, "sys_list_sessions", source, "powershell", "List active PowerShell sessions.");
        }
    }

    private static void AddToolItem(
        List<SystemToolCatalogItem> items,
        HashSet<string> seen,
        string toolName,
        string source,
        string category,
        string description)
    {
        if (!seen.Add(toolName))
        {
            return;
        }

        items.Add(new SystemToolCatalogItem(
            toolName,
            source,
            category,
            description,
            GuessCapability(toolName, description),
            GuessRiskLevel(toolName, description),
            string.Empty));
    }

    private static void AddTool(
        List<SystemToolCatalogItem> items,
        HashSet<string> seen,
        AITool tool,
        string source,
        string category)
    {
        if (string.IsNullOrWhiteSpace(tool.Name)
            || IsReservedSystemToolName(tool.Name)
            || !seen.Add(tool.Name))
        {
            return;
        }

        items.Add(new SystemToolCatalogItem(
            tool.Name,
            source,
            category,
            tool.Description ?? string.Empty,
            GuessCapability(tool.Name, tool.Description),
            GuessRiskLevel(tool.Name, tool.Description),
            BuildParameterSummary(tool)));
    }

    private static string GuessCategory(string? toolName, string source)
    {
        if (source.Contains("PowerShell", StringComparison.OrdinalIgnoreCase)
            || (toolName?.Contains("powershell", StringComparison.OrdinalIgnoreCase) ?? false)
            || (toolName?.Contains("session", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return "powershell";
        }

        if (source.Contains("File", StringComparison.OrdinalIgnoreCase)
            || (toolName?.StartsWith("sys_", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return "file";
        }

        if (source.StartsWith("mcp:", StringComparison.OrdinalIgnoreCase))
        {
            return "mcp";
        }

        if (source.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase))
        {
            return "plugin";
        }

        return "system";
    }

    private static bool IsReservedSystemToolName(string toolName)
        => toolName.StartsWith("sys_", StringComparison.OrdinalIgnoreCase);

    private static string GuessCapability(string? toolName, string? description)
    {
        var text = $"{toolName} {description}";
        if (ContainsAny(text, "read", "list", "get", "search", "读取", "查询", "搜索"))
        {
            return "read";
        }

        if (ContainsAny(text, "write", "create", "edit", "delete", "move", "execute", "run", "start",
                "写入", "创建", "编辑", "删除", "执行", "启动"))
        {
            return "execute";
        }

        return "general";
    }

    private static string GuessRiskLevel(string? toolName, string? description)
    {
        var text = $"{toolName} {description}";
        return ContainsAny(text, "delete", "write", "create", "edit", "move", "execute", "run", "start", "send",
            "删除", "写入", "创建", "编辑", "移动", "执行", "启动")
            ? "high"
            : "low";
    }

    private static string BuildParameterSummary(AITool tool)
    {
        var schemaSummary = BuildParameterSummaryFromSchema(tool);
        if (!string.IsNullOrWhiteSpace(schemaSummary))
        {
            return schemaSummary;
        }

        if (string.IsNullOrWhiteSpace(tool.Description))
        {
            return string.Empty;
        }

        var description = tool.Description.Trim();
        foreach (var marker in ParameterSectionMarkers)
        {
            var index = description.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var summary = description[index..].Trim();
            return summary.Length <= 240 ? summary : $"{summary[..240].TrimEnd()}...";
        }

        return string.Empty;
    }

    private static string BuildParameterSummaryFromSchema(AITool tool)
    {
        if (tool is not AIFunctionDeclaration function
            || function.JsonSchema.ValueKind != JsonValueKind.Object
            || !function.JsonSchema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var requiredNames = ReadRequiredNames(function.JsonSchema);
        var parts = new List<string>();
        foreach (var property in properties.EnumerateObject())
        {
            var schema = property.Value;
            var type = ReadSchemaType(schema);
            var required = requiredNames.Contains(property.Name) ? "required" : "optional";
            var description = schema.TryGetProperty("description", out var descriptionElement)
                && descriptionElement.ValueKind == JsonValueKind.String
                    ? descriptionElement.GetString()
                    : null;

            parts.Add(string.IsNullOrWhiteSpace(description)
                ? $"{property.Name}: {type}, {required}"
                : $"{property.Name}: {type}, {required}, {description}");
        }

        if (parts.Count == 0)
        {
            return string.Empty;
        }

        var summary = string.Join("; ", parts);
        return summary.Length <= 240 ? summary : $"{summary[..240].TrimEnd()}...";
    }

    private static HashSet<string> ReadRequiredNames(JsonElement schema)
    {
        var requiredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!schema.TryGetProperty("required", out var required)
            || required.ValueKind != JsonValueKind.Array)
        {
            return requiredNames;
        }

        foreach (var item in required.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
            {
                requiredNames.Add(name);
            }
        }

        return requiredNames;
    }

    private static string ReadSchemaType(JsonElement schema)
    {
        if (schema.TryGetProperty("type", out var type))
        {
            if (type.ValueKind == JsonValueKind.String)
            {
                return type.GetString() ?? "value";
            }

            if (type.ValueKind == JsonValueKind.Array)
            {
                var types = type.EnumerateArray()
                    .Where(static item => item.ValueKind == JsonValueKind.String)
                    .Select(static item => item.GetString())
                    .Where(static item => !string.IsNullOrWhiteSpace(item))
                    .Select(static item => item!)
                    .ToArray();

                if (types.Length > 0)
                {
                    return string.Join("|", types);
                }
            }
        }

        if (schema.TryGetProperty("enum", out _))
        {
            return "enum";
        }

        if (schema.TryGetProperty("properties", out _))
        {
            return "object";
        }

        if (schema.TryGetProperty("items", out _))
        {
            return "array";
        }

        return "value";
    }

    internal static string BuildParameterSummaryForTest(AITool tool) => BuildParameterSummary(tool);

    private static bool ContainsAny(string text, params string[] words)
    {
        foreach (var word in words)
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
