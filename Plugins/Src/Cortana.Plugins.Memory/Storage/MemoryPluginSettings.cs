using System.Text.Json;

namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// 记忆插件初始化配置。
/// </summary>
public sealed class MemoryPluginSettings
{
    /// <summary>
    /// 存储配置。
    /// </summary>
    public MemoryStorageSettings Storage { get; init; } = new();

    /// <summary>
    /// 运行时配置。
    /// </summary>
    public MemoryRuntimeSettings Runtime { get; init; } = new();
}

/// <summary>
/// 记忆存储配置。
/// </summary>
public sealed class MemoryStorageSettings
{
    /// <summary>
    /// 存储提供程序。当前支持 sqlite，预留 sqlserver。
    /// </summary>
    public string Provider { get; init; } = "sqlite";

    /// <summary>
    /// SQLite 存储配置。
    /// </summary>
    public MemorySqliteSettings Sqlite { get; init; } = new();

    /// <summary>
    /// SQL Server 存储配置，当前仅预留。
    /// </summary>
    public MemorySqlServerSettings SqlServer { get; init; } = new();
}

/// <summary>
/// SQLite 存储配置。
/// </summary>
public sealed class MemorySqliteSettings
{
    /// <summary>
    /// 数据目录；为空时使用插件数据目录。
    /// </summary>
    public string DataDirectory { get; init; } = string.Empty;

    /// <summary>
    /// 数据库文件名。
    /// </summary>
    public string DatabaseFileName { get; init; } = "memory.db";

    /// <summary>
    /// SQLite 连接字符串；为空时由数据目录和文件名生成。
    /// </summary>
    public string ConnectionString { get; init; } = string.Empty;
}

/// <summary>
/// SQL Server 存储配置。
/// </summary>
public sealed class MemorySqlServerSettings
{
    /// <summary>
    /// SQL Server 连接字符串。
    /// </summary>
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>
    /// 数据库 Schema。
    /// </summary>
    public string Schema { get; init; } = "dbo";
}

/// <summary>
/// 记忆运行时配置。
/// </summary>
public sealed class MemoryRuntimeSettings
{
    /// <summary>
    /// 是否启用自动后台处理。
    /// </summary>
    public bool EnableAutoProcessing { get; init; } = true;
}

/// <summary>
/// 加载记忆插件 setting.json 初始化配置。
/// </summary>
public static class MemoryPluginSettingsLoader
{
    /// <summary>
    /// 从指定数据目录、应用程序目录或当前目录加载 setting.json。
    /// </summary>
    public static MemoryPluginSettings Load(string? dataDirectory = null)
    {
        var path = ResolvePath(dataDirectory);
        if (path is null) return new MemoryPluginSettings();

        try
        {
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            return ReadSettings(document.RootElement);
        }
        catch (JsonException)
        {
            return new MemoryPluginSettings();
        }
        catch (IOException)
        {
            return new MemoryPluginSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new MemoryPluginSettings();
        }
    }

    private static string? ResolvePath(string? dataDirectory)
    {
        var candidates = new[]
        {
            string.IsNullOrWhiteSpace(dataDirectory) ? null : Path.Combine(dataDirectory, "setting.json"),
            Path.Combine(AppContext.BaseDirectory, "setting.json"),
            Path.Combine(Environment.CurrentDirectory, "setting.json")
        };

        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private static MemoryPluginSettings ReadSettings(JsonElement root)
    {
        var storage = root.TryGetProperty("storage", out var storageElement) && storageElement.ValueKind == JsonValueKind.Object
            ? ReadStorageSettings(storageElement)
            : new MemoryStorageSettings();
        var runtime = root.TryGetProperty("runtime", out var runtimeElement) && runtimeElement.ValueKind == JsonValueKind.Object
            ? ReadRuntimeSettings(runtimeElement)
            : new MemoryRuntimeSettings();

        return new MemoryPluginSettings
        {
            Storage = storage,
            Runtime = runtime
        };
    }

    private static MemoryStorageSettings ReadStorageSettings(JsonElement storage)
    {
        return new MemoryStorageSettings
        {
            Provider = GetString(storage, "provider", "sqlite"),
            Sqlite = storage.TryGetProperty("sqlite", out var sqlite) && sqlite.ValueKind == JsonValueKind.Object
                ? ReadSqliteSettings(sqlite)
                : new MemorySqliteSettings(),
            SqlServer = storage.TryGetProperty("sqlServer", out var sqlServer) && sqlServer.ValueKind == JsonValueKind.Object
                ? ReadSqlServerSettings(sqlServer)
                : new MemorySqlServerSettings()
        };
    }

    private static MemorySqliteSettings ReadSqliteSettings(JsonElement sqlite)
    {
        return new MemorySqliteSettings
        {
            DataDirectory = GetString(sqlite, "dataDirectory", string.Empty),
            DatabaseFileName = GetString(sqlite, "databaseFileName", "memory.db"),
            ConnectionString = GetString(sqlite, "connectionString", string.Empty)
        };
    }

    private static MemorySqlServerSettings ReadSqlServerSettings(JsonElement sqlServer)
    {
        return new MemorySqlServerSettings
        {
            ConnectionString = GetString(sqlServer, "connectionString", string.Empty),
            Schema = GetString(sqlServer, "schema", "dbo")
        };
    }

    private static MemoryRuntimeSettings ReadRuntimeSettings(JsonElement runtime)
    {
        return new MemoryRuntimeSettings
        {
            EnableAutoProcessing = GetBoolean(runtime, "enableAutoProcessing", true)
        };
    }

    private static string GetString(JsonElement element, string propertyName, string fallback)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    private static bool GetBoolean(JsonElement element, string propertyName, bool fallback)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback
        };
    }
}
