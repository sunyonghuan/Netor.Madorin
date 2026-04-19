using Microsoft.Data.Sqlite;

namespace Netor.Cortana.Entitys
{
    /// <summary>
    /// Cortana 的数据库上下文，基于 SQLite 的轻量级嵌入式关系数据库。
    /// 纯 P/Invoke 实现，AOT 安全，无反射依赖。
    /// </summary>
    /// <remarks>
    /// <para>数据库文件默认保存在应用当前目录下：</para>
    /// <para><c>cortana.db</c></para>
    /// <para>典型用法：</para>
    /// <code>
    /// using var db = new CortanaDbContext();
    /// var providers = providerService.GetAll();
    /// </code>
    /// </remarks>
    public sealed class CortanaDbContext : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly object _lock = new();

        /// <summary>
        /// 使用默认数据库路径初始化上下文。
        /// </summary>
        public CortanaDbContext()
            : this(GetDefaultDbPath())
        {
        }

        /// <summary>
        /// 使用指定的数据库文件路径初始化上下文。
        /// </summary>
        /// <param name="dbPath">数据库文件的完整路径</param>
        public CortanaDbContext(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
                throw new ArgumentException("Database path cannot be null or empty.", nameof(dbPath));

            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _connection = new SqliteConnection($"Data Source={dbPath}");
            _connection.Open();

            // WAL 模式：支持并发读、提升写入性能
            Execute("PRAGMA journal_mode=WAL;");

            EnsureTables();
            EnsureMigrations();
            EnsureIndexes();
        }

        // ──────── 辅助查询方法（供 Service 层调用） ────────

        /// <summary>
        /// 执行非查询语句（INSERT / UPDATE / DELETE），返回受影响行数。
        /// </summary>
        public int Execute(string sql, Action<SqliteCommand>? bind = null)
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                bind?.Invoke(cmd);
                return cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 执行标量查询，返回单个值。
        /// </summary>
        public T? ExecuteScalar<T>(string sql, Action<SqliteCommand>? bind = null)
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                bind?.Invoke(cmd);
                var result = cmd.ExecuteScalar();
                if (result is null or DBNull) return default;
                if (result is T typed) return typed;
                if (typeof(T) == typeof(string)) return (T)(object)result.ToString()!;
                if (typeof(T) == typeof(long)) return (T)(object)Convert.ToInt64(result);
                if (typeof(T) == typeof(int)) return (T)(object)Convert.ToInt32(result);
                if (typeof(T) == typeof(double)) return (T)(object)Convert.ToDouble(result);
                return default;
            }
        }

        /// <summary>
        /// 执行查询并将结果映射为列表。
        /// </summary>
        public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, Action<SqliteCommand>? bind = null)
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                bind?.Invoke(cmd);
                using var reader = cmd.ExecuteReader();
                var list = new List<T>();
                while (reader.Read())
                    list.Add(map(reader));
                return list;
            }
        }

        /// <summary>
        /// 执行查询并返回第一行，无结果时返回 null。
        /// </summary>
        public T? QueryFirstOrDefault<T>(string sql, Func<SqliteDataReader, T> map, Action<SqliteCommand>? bind = null) where T : class
        {
            lock (_lock)
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = sql;
                bind?.Invoke(cmd);
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? map(reader) : null;
            }
        }

        /// <summary>
        /// 在事务中批量执行操作。
        /// </summary>
        public void ExecuteInTransaction(Action<SqliteConnection> action)
        {
            lock (_lock)
            {
                using var transaction = _connection.BeginTransaction();
                try
                {
                    action(_connection);
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }

        // ──────── 表结构创建 ────────

        private void EnsureTables()
        {
            Execute("""
                CREATE TABLE IF NOT EXISTS AiProviders (
                    Id TEXT PRIMARY KEY,
                    CreatedTimestamp INTEGER NOT NULL,
                    UpdatedTimestamp INTEGER NOT NULL,
                    Name TEXT NOT NULL DEFAULT '',
                    Url TEXT NOT NULL DEFAULT '',
                    Key TEXT NOT NULL DEFAULT '',
                    AuthToken TEXT NOT NULL DEFAULT '',
                    Description TEXT NOT NULL DEFAULT '',
                    ProviderType TEXT NOT NULL DEFAULT 'OpenAI',
                    IsDefault INTEGER NOT NULL DEFAULT 0,
                    IsEnabled INTEGER NOT NULL DEFAULT 1,
                    SortOrder INTEGER NOT NULL DEFAULT 0
                );
                """);

            Execute("""
                CREATE TABLE IF NOT EXISTS AiModels (
                    Id TEXT PRIMARY KEY,
                    CreatedTimestamp INTEGER NOT NULL,
                    UpdatedTimestamp INTEGER NOT NULL,
                    Name TEXT NOT NULL DEFAULT '',
                    DisplayName TEXT NOT NULL DEFAULT '',
                    Description TEXT NOT NULL DEFAULT '',
                    ContextLength INTEGER NOT NULL DEFAULT 0,
                    ModelType TEXT NOT NULL DEFAULT 'chat',
                    IsDefault INTEGER NOT NULL DEFAULT 0,
                    IsEnabled INTEGER NOT NULL DEFAULT 1,
                    ProviderId TEXT NOT NULL DEFAULT ''
                );
                """);

            Execute("""
                CREATE TABLE IF NOT EXISTS Agents (
                    Id TEXT PRIMARY KEY,
                    CreatedTimestamp INTEGER NOT NULL,
                    UpdatedTimestamp INTEGER NOT NULL,
                    Name TEXT NOT NULL DEFAULT '',
                    Instructions TEXT NOT NULL DEFAULT '',
                    Description TEXT NOT NULL DEFAULT '',
                    Image TEXT NOT NULL DEFAULT '',
                    Temperature REAL NOT NULL DEFAULT 0.7,
                    MaxTokens INTEGER NOT NULL DEFAULT 0,
                    TopP REAL NOT NULL DEFAULT 1.0,
                    FrequencyPenalty REAL NOT NULL DEFAULT 0,
                    PresencePenalty REAL NOT NULL DEFAULT 0,
                    MaxHistoryMessages INTEGER NOT NULL DEFAULT 0,
                    IsDefault INTEGER NOT NULL DEFAULT 0,
                    IsEnabled INTEGER NOT NULL DEFAULT 1,
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    EnabledPluginIds TEXT NOT NULL DEFAULT '[]',
                    EnabledMcpServerIds TEXT NOT NULL DEFAULT '[]'
                );
                """);

            Execute("""
                CREATE TABLE IF NOT EXISTS ChatSessions (
                    Id TEXT PRIMARY KEY,
                    CreatedTimestamp INTEGER NOT NULL,
                    UpdatedTimestamp INTEGER NOT NULL,
                    Categorize TEXT NOT NULL DEFAULT '',
                    Title TEXT NOT NULL DEFAULT '',
                    Summary TEXT NOT NULL DEFAULT '',
                    RawDiscription TEXT NOT NULL DEFAULT '',
                    AgentId TEXT NOT NULL DEFAULT '',
                    IsArchived INTEGER NOT NULL DEFAULT 0,
                    IsPinned INTEGER NOT NULL DEFAULT 0,
                    LastActiveTimestamp INTEGER NOT NULL DEFAULT 0,
                    TotalTokenCount INTEGER NOT NULL DEFAULT 0,
                    CompactedContext TEXT NOT NULL DEFAULT '',
                    CompactedAtCount INTEGER NOT NULL DEFAULT 0
                );
                """);

            Execute("""
                CREATE TABLE IF NOT EXISTS ChatMessages (
                    Id TEXT PRIMARY KEY,
                    CreatedTimestamp INTEGER NOT NULL,
                    UpdatedTimestamp INTEGER NOT NULL,
                    SessionId TEXT NOT NULL DEFAULT '',
                    Role TEXT NOT NULL DEFAULT '',
                    AuthorName TEXT NOT NULL DEFAULT '',
                    Content TEXT NOT NULL DEFAULT '',
                    TokenCount INTEGER NOT NULL DEFAULT 0,
                    ModelName TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT
                );
                """);

            Execute("""
                CREATE TABLE IF NOT EXISTS McpServers (
                    Id TEXT PRIMARY KEY,
                    CreatedTimestamp INTEGER NOT NULL,
                    UpdatedTimestamp INTEGER NOT NULL,
                    Name TEXT NOT NULL DEFAULT '',
                    TransportType TEXT NOT NULL DEFAULT 'stdio',
                    Command TEXT NOT NULL DEFAULT '',
                    Arguments TEXT NOT NULL DEFAULT '[]',
                    Url TEXT NOT NULL DEFAULT '',
                    ApiKey TEXT NOT NULL DEFAULT '',
                    EnvironmentVariables TEXT NOT NULL DEFAULT '{}',
                    Description TEXT NOT NULL DEFAULT '',
                    IsEnabled INTEGER NOT NULL DEFAULT 1
                );
                """);

            Execute("""
                CREATE TABLE IF NOT EXISTS SystemSettings (
                    Id TEXT PRIMARY KEY,
                    CreatedTimestamp INTEGER NOT NULL,
                    UpdatedTimestamp INTEGER NOT NULL,
                    [Group] TEXT NOT NULL DEFAULT '',
                    DisplayName TEXT NOT NULL DEFAULT '',
                    Description TEXT NOT NULL DEFAULT '',
                    Value TEXT NOT NULL DEFAULT '',
                    DefaultValue TEXT NOT NULL DEFAULT '',
                    ValueType TEXT NOT NULL DEFAULT 'string',
                    SortOrder INTEGER NOT NULL DEFAULT 0
                );
                """);

            Execute("""
                CREATE TABLE IF NOT EXISTS ChatMessageAssets (
                    Id TEXT PRIMARY KEY,
                    CreatedTimestamp INTEGER NOT NULL,
                    UpdatedTimestamp INTEGER NOT NULL,
                    SessionId TEXT NOT NULL DEFAULT '',
                    MessageId TEXT NOT NULL DEFAULT '',
                    Role TEXT NOT NULL DEFAULT '',
                    AssetGroup TEXT NOT NULL DEFAULT '',
                    AssetKind TEXT NOT NULL DEFAULT '',
                    MimeType TEXT NOT NULL DEFAULT '',
                    OriginalName TEXT NOT NULL DEFAULT '',
                    RelativePath TEXT NOT NULL DEFAULT '',
                    FileSizeBytes INTEGER NOT NULL DEFAULT 0,
                    Sha256 TEXT NOT NULL DEFAULT '',
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    Width INTEGER NOT NULL DEFAULT 0,
                    Height INTEGER NOT NULL DEFAULT 0,
                    DurationMs INTEGER NOT NULL DEFAULT 0,
                    SourceType TEXT NOT NULL DEFAULT '',
                    Status TEXT NOT NULL DEFAULT 'active'
                );
                """);

            Execute("""
                CREATE TABLE IF NOT EXISTS CompactionSegments (
                    Id TEXT PRIMARY KEY,
                    CreatedTimestamp INTEGER NOT NULL,
                    UpdatedTimestamp INTEGER NOT NULL,
                    SessionId TEXT NOT NULL DEFAULT '',
                    SegmentIndex INTEGER NOT NULL DEFAULT 0,
                    StartMessageIndex INTEGER NOT NULL DEFAULT 0,
                    EndMessageIndex INTEGER NOT NULL DEFAULT 0,
                    Summary TEXT NOT NULL DEFAULT '',
                    OriginalMessageCount INTEGER NOT NULL DEFAULT 0,
                    ModelName TEXT NOT NULL DEFAULT ''
                );
                """);
        }

        /// <summary>
        /// 创建常用查询索引，提升查询性能。
        /// 对已有数据库执行增量列迁移。
        /// SQLite 不支持 IF NOT EXISTS 语法添加列，因此使用 try-catch 跳过已存在的列。
        /// </summary>
        private void EnsureMigrations()
        {
            TryAddColumn("ALTER TABLE AiProviders ADD COLUMN AuthToken TEXT NOT NULL DEFAULT ''");
            TryAddColumn("ALTER TABLE ChatSessions ADD COLUMN CompactedContext TEXT NOT NULL DEFAULT ''");
            TryAddColumn("ALTER TABLE ChatSessions ADD COLUMN CompactedAtCount INTEGER NOT NULL DEFAULT 0");

            // v1.2: AiModels 能力字段
            TryAddColumn("ALTER TABLE AiModels ADD COLUMN InputCapabilities INTEGER NOT NULL DEFAULT 1");
            TryAddColumn("ALTER TABLE AiModels ADD COLUMN OutputCapabilities INTEGER NOT NULL DEFAULT 1");
            TryAddColumn("ALTER TABLE AiModels ADD COLUMN InteractionCapabilities INTEGER NOT NULL DEFAULT 0");
            TryAddColumn("ALTER TABLE AiModels ADD COLUMN CapabilitySource TEXT NOT NULL DEFAULT 'manual'");
            TryAddColumn("ALTER TABLE AiModels ADD COLUMN CapabilityNotes TEXT NOT NULL DEFAULT ''");

            // v1.2: Agents 子智能体字段
            TryAddColumn("ALTER TABLE Agents ADD COLUMN Avatar TEXT NOT NULL DEFAULT ''");
            TryAddColumn("ALTER TABLE Agents ADD COLUMN DefaultProviderId TEXT NOT NULL DEFAULT ''");
            TryAddColumn("ALTER TABLE Agents ADD COLUMN DefaultModelId TEXT NOT NULL DEFAULT ''");
        }

        private void TryAddColumn(string alterSql)
        {
            try { Execute(alterSql); }
            catch (SqliteException) { /* 列已存在，忽略 */ }
        }

        /// <summary>
        /// CREATE INDEX IF NOT EXISTS 是幂等操作。
        /// </summary>
        private void EnsureIndexes()
        {
            Execute("CREATE INDEX IF NOT EXISTS IX_AiModels_ProviderId ON AiModels(ProviderId);");
            Execute("CREATE INDEX IF NOT EXISTS IX_Agents_IsEnabled ON Agents(IsEnabled);");
            Execute("CREATE INDEX IF NOT EXISTS IX_Agents_SortOrder ON Agents(SortOrder);");
            Execute("CREATE INDEX IF NOT EXISTS IX_AiProviders_IsEnabled ON AiProviders(IsEnabled);");
            Execute("CREATE INDEX IF NOT EXISTS IX_ChatSessions_AgentId ON ChatSessions(AgentId);");
            Execute("CREATE INDEX IF NOT EXISTS IX_ChatSessions_LastActive ON ChatSessions(LastActiveTimestamp);");
            Execute("CREATE INDEX IF NOT EXISTS IX_ChatMessages_SessionId ON ChatMessages(SessionId);");
            Execute("CREATE INDEX IF NOT EXISTS IX_McpServers_IsEnabled ON McpServers(IsEnabled);");
            Execute("CREATE INDEX IF NOT EXISTS IX_SystemSettings_Group ON SystemSettings([Group]);");
            Execute("CREATE INDEX IF NOT EXISTS IX_SystemSettings_SortOrder ON SystemSettings(SortOrder);");

            // ChatMessageAssets 索引
            Execute("CREATE INDEX IF NOT EXISTS IX_ChatMessageAssets_SessionId ON ChatMessageAssets(SessionId);");
            Execute("CREATE INDEX IF NOT EXISTS IX_ChatMessageAssets_MessageId ON ChatMessageAssets(MessageId);");
            Execute("CREATE INDEX IF NOT EXISTS IX_ChatMessageAssets_Session_Role ON ChatMessageAssets(SessionId, Role);");

            // CompactionSegments 索引
            Execute("CREATE INDEX IF NOT EXISTS IX_CompactionSegments_SessionId ON CompactionSegments(SessionId);");
            Execute("CREATE INDEX IF NOT EXISTS IX_CompactionSegments_Session_Index ON CompactionSegments(SessionId, SegmentIndex);");
        }

        /// <summary>
        /// 获取默认的数据库文件路径。
        /// </summary>
        /// <returns>数据库文件的完整路径</returns>
        public static string GetDefaultDbPath()
        {
            var folder = Environment.CurrentDirectory;

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            return Path.Combine(folder, "cortana.db");
        }

        /// <summary>
        /// 释放数据库连接资源。
        /// </summary>
        public void Dispose()
        {
            _connection?.Dispose();
        }
    }
}