using System.Text.Json;

namespace Cortana.Plugins.Memory.Mcp;

/// <summary>
/// 解析 MCP 独立运行模式配置。
/// </summary>
public static class MemoryMcpRuntimeOptionsLoader
{
    /// <summary>
    /// 按 CLI、环境变量、程序目录配置文件、内置默认值的顺序加载配置。
    /// </summary>
    public static MemoryMcpRuntimeOptions Load(string[] args)
    {
        var configPath = GetArgumentValue(args, "--config")
            ?? Path.Combine(AppContext.BaseDirectory, "config.json");

        var options = LoadFromFile(configPath) ?? new MemoryMcpRuntimeOptions();

        options = WithDataDirectory(options, Environment.GetEnvironmentVariable("CORTANA_MEMORY_DATA_DIR"));
        options = WithDefaultAgentId(options, Environment.GetEnvironmentVariable("CORTANA_MEMORY_AGENT_ID"));
        options = WithDefaultWorkspaceId(options, Environment.GetEnvironmentVariable("CORTANA_MEMORY_WORKSPACE_ID"));

        options = WithDataDirectory(options, GetArgumentValue(args, "--data-dir"));
        options = WithDefaultAgentId(options, GetArgumentValue(args, "--agent-id"));
        options = WithDefaultWorkspaceId(options, GetArgumentValue(args, "--workspace-id"));

        return Normalize(options);
    }

    private static MemoryMcpRuntimeOptions? LoadFromFile(string path)
    {
        if (!File.Exists(path)) return null;

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var options = new MemoryMcpRuntimeOptions();
        options = WithDataDirectory(options, GetString(root, "dataDirectory"));
        options = WithDatabaseFileName(options, GetString(root, "databaseFileName"));
        options = WithDefaultAgentId(options, GetString(root, "defaultAgentId"));
        options = WithDefaultWorkspaceId(options, GetString(root, "defaultWorkspaceId"));
        options = WithDefaultSource(options, GetString(root, "defaultSource"));
        options = WithEnableAutoProcessing(options, GetBoolean(root, "enableAutoProcessing"));
        return options;
    }

    private static string? GetArgumentValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static string? GetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool? GetBoolean(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }

    private static MemoryMcpRuntimeOptions Normalize(MemoryMcpRuntimeOptions options)
    {
        return new MemoryMcpRuntimeOptions
        {
            DataDirectory = string.IsNullOrWhiteSpace(options.DataDirectory)
                ? Path.Combine(AppContext.BaseDirectory, "data")
                : Path.GetFullPath(options.DataDirectory),
            DatabaseFileName = string.IsNullOrWhiteSpace(options.DatabaseFileName) ? "memory.db" : options.DatabaseFileName,
            DefaultAgentId = string.IsNullOrWhiteSpace(options.DefaultAgentId) ? "mcp-default" : options.DefaultAgentId,
            DefaultWorkspaceId = string.IsNullOrWhiteSpace(options.DefaultWorkspaceId) ? "default" : options.DefaultWorkspaceId,
            DefaultSource = string.IsNullOrWhiteSpace(options.DefaultSource) ? "mcp" : options.DefaultSource,
            EnableAutoProcessing = options.EnableAutoProcessing
        };
    }

    private static MemoryMcpRuntimeOptions WithDataDirectory(MemoryMcpRuntimeOptions options, string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? options : new MemoryMcpRuntimeOptions
        {
            DataDirectory = value,
            DatabaseFileName = options.DatabaseFileName,
            DefaultAgentId = options.DefaultAgentId,
            DefaultWorkspaceId = options.DefaultWorkspaceId,
            DefaultSource = options.DefaultSource,
            EnableAutoProcessing = options.EnableAutoProcessing
        };
    }

    private static MemoryMcpRuntimeOptions WithDatabaseFileName(MemoryMcpRuntimeOptions options, string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? options : new MemoryMcpRuntimeOptions
        {
            DataDirectory = options.DataDirectory,
            DatabaseFileName = value,
            DefaultAgentId = options.DefaultAgentId,
            DefaultWorkspaceId = options.DefaultWorkspaceId,
            DefaultSource = options.DefaultSource,
            EnableAutoProcessing = options.EnableAutoProcessing
        };
    }

    private static MemoryMcpRuntimeOptions WithDefaultAgentId(MemoryMcpRuntimeOptions options, string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? options : new MemoryMcpRuntimeOptions
        {
            DataDirectory = options.DataDirectory,
            DatabaseFileName = options.DatabaseFileName,
            DefaultAgentId = value,
            DefaultWorkspaceId = options.DefaultWorkspaceId,
            DefaultSource = options.DefaultSource,
            EnableAutoProcessing = options.EnableAutoProcessing
        };
    }

    private static MemoryMcpRuntimeOptions WithDefaultWorkspaceId(MemoryMcpRuntimeOptions options, string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? options : new MemoryMcpRuntimeOptions
        {
            DataDirectory = options.DataDirectory,
            DatabaseFileName = options.DatabaseFileName,
            DefaultAgentId = options.DefaultAgentId,
            DefaultWorkspaceId = value,
            DefaultSource = options.DefaultSource,
            EnableAutoProcessing = options.EnableAutoProcessing
        };
    }

    private static MemoryMcpRuntimeOptions WithDefaultSource(MemoryMcpRuntimeOptions options, string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? options : new MemoryMcpRuntimeOptions
        {
            DataDirectory = options.DataDirectory,
            DatabaseFileName = options.DatabaseFileName,
            DefaultAgentId = options.DefaultAgentId,
            DefaultWorkspaceId = options.DefaultWorkspaceId,
            DefaultSource = value,
            EnableAutoProcessing = options.EnableAutoProcessing
        };
    }

    private static MemoryMcpRuntimeOptions WithEnableAutoProcessing(MemoryMcpRuntimeOptions options, bool? value)
    {
        return value is null ? options : new MemoryMcpRuntimeOptions
        {
            DataDirectory = options.DataDirectory,
            DatabaseFileName = options.DatabaseFileName,
            DefaultAgentId = options.DefaultAgentId,
            DefaultWorkspaceId = options.DefaultWorkspaceId,
            DefaultSource = options.DefaultSource,
            EnableAutoProcessing = value.Value
        };
    }
}
