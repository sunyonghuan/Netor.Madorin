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
                    ContentsJson TEXT NOT NULL DEFAULT '',
                    TokenCount INTEGER NOT NULL DEFAULT 0,
                    ModelName TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT,
                    AgentId TEXT NOT NULL DEFAULT '',
                    AgentName TEXT NOT NULL DEFAULT ''
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
                CREATE TABLE IF NOT EXISTS GlobalPlugins (
                    Id TEXT PRIMARY KEY,
                    CreatedTimestamp INTEGER NOT NULL,
                    UpdatedTimestamp INTEGER NOT NULL,
                    PluginId TEXT NOT NULL UNIQUE,
                    IsEnabled INTEGER NOT NULL DEFAULT 1
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

            // ========================================
            // 阶段 2B 新增：Workflow 任务相关 4 张表
            // 详见 docs/未来版本策划/多智能体编排模式策划/06-工作模式独立模块设计.md §3
            // ========================================

            Execute("""
                CREATE TABLE IF NOT EXISTS OrchestrationTask (
                    Id TEXT PRIMARY KEY,
                    CreatedTimestamp INTEGER NOT NULL,
                    UpdatedTimestamp INTEGER NOT NULL,
                    Title TEXT NOT NULL DEFAULT '',
                    Summary TEXT NOT NULL DEFAULT '',
                    IsTitleAutoGenerated INTEGER NOT NULL DEFAULT 0,
                    Mode TEXT NOT NULL DEFAULT '',
                    SubMode TEXT NOT NULL DEFAULT '',
                    Status TEXT NOT NULL DEFAULT '',
                    StartedAt INTEGER NOT NULL DEFAULT 0,
                    CompletedAt INTEGER NULL,
                    LastActiveTimestamp INTEGER NOT NULL DEFAULT 0,
                    FinalReport TEXT NULL,
                    TraceId TEXT NOT NULL DEFAULT '',
                    CreatedBy TEXT NOT NULL DEFAULT '',
                    SourceSessionId TEXT NULL,
                    SourceTaskId TEXT NULL,
                    ManagerAgentId TEXT NULL,
                    ManagerAgentName TEXT NULL,
                    InitialInput TEXT NOT NULL DEFAULT '',
                    InitialAttachmentsJson TEXT NULL,
                    ErrorMessage TEXT NULL,
                    WorkspaceId TEXT NOT NULL DEFAULT '',
                    IsPinned INTEGER NOT NULL DEFAULT 0,
                    IsArchived INTEGER NOT NULL DEFAULT 0,
                    TotalTokenCount INTEGER NOT NULL DEFAULT 0,
                    OverridesJson TEXT NULL
                );
                """);

            Execute("""
                CREATE TABLE IF NOT EXISTS OrchestrationParticipant (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TaskId TEXT NOT NULL,
                    AgentId TEXT NOT NULL,
                    AgentName TEXT NOT NULL DEFAULT '',
                    Role TEXT NOT NULL DEFAULT '',
                    JoinedAt INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (TaskId) REFERENCES OrchestrationTask(Id) ON DELETE CASCADE
                );
                """);

            Execute("""
                CREATE TABLE IF NOT EXISTS OrchestrationStep (
                    Id TEXT PRIMARY KEY,
                    TaskId TEXT NOT NULL,
                    ParentStepId TEXT NULL,
                    Sequence INTEGER NOT NULL DEFAULT 0,
                    AgentId TEXT NULL,
                    AgentName TEXT NULL,
                    Action TEXT NOT NULL DEFAULT '',
                    Status TEXT NOT NULL DEFAULT '',
                    StartedAt INTEGER NOT NULL DEFAULT 0,
                    CompletedAt INTEGER NULL,
                    DurationMs INTEGER NULL,
                    TokenInputCount INTEGER NULL,
                    TokenOutputCount INTEGER NULL,
                    ErrorMessage TEXT NULL,
                    SummaryJson TEXT NULL,
                    FOREIGN KEY (TaskId) REFERENCES OrchestrationTask(Id) ON DELETE CASCADE,
                    FOREIGN KEY (ParentStepId) REFERENCES OrchestrationStep(Id) ON DELETE SET NULL
                );
                """);

            Execute("""
                CREATE TABLE IF NOT EXISTS OrchestrationMessage (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TaskId TEXT NOT NULL,
                    StepId TEXT NOT NULL,
                    Sequence INTEGER NOT NULL DEFAULT 0,
                    Role TEXT NOT NULL DEFAULT '',
                    AuthorName TEXT NULL,
                    Content TEXT NOT NULL DEFAULT '',
                    CreatedAt INTEGER NOT NULL DEFAULT 0,
                    AttachmentsJson TEXT NULL,
                    FOREIGN KEY (TaskId) REFERENCES OrchestrationTask(Id) ON DELETE CASCADE,
                    FOREIGN KEY (StepId) REFERENCES OrchestrationStep(Id) ON DELETE CASCADE
                );
                """);

            // Workflow 任务索引（与文档 §3.1 / §3.2 / §3.3 / §3.4 对齐）
            Execute("CREATE INDEX IF NOT EXISTS IX_OrchestrationTask_Status ON OrchestrationTask(Status);");
            Execute("CREATE INDEX IF NOT EXISTS IX_OrchestrationTask_LastActiveTimestamp ON OrchestrationTask(LastActiveTimestamp);");
            Execute("CREATE INDEX IF NOT EXISTS IX_OrchestrationTask_Pinned_Active ON OrchestrationTask(IsPinned DESC, LastActiveTimestamp DESC);");
            Execute("CREATE INDEX IF NOT EXISTS IX_OrchestrationTask_SourceSessionId ON OrchestrationTask(SourceSessionId);");
            Execute("CREATE INDEX IF NOT EXISTS IX_OrchestrationTask_WorkspaceId ON OrchestrationTask(WorkspaceId);");
            Execute("CREATE INDEX IF NOT EXISTS IX_OrchestrationTask_SourceTaskId ON OrchestrationTask(SourceTaskId);");
            Execute("CREATE INDEX IF NOT EXISTS IX_OrchestrationParticipant_TaskId ON OrchestrationParticipant(TaskId);");
            Execute("CREATE INDEX IF NOT EXISTS IX_OrchestrationParticipant_AgentId ON OrchestrationParticipant(AgentId);");
            Execute("CREATE INDEX IF NOT EXISTS IX_OrchestrationStep_TaskId_Sequence ON OrchestrationStep(TaskId, Sequence);");
            Execute("CREATE INDEX IF NOT EXISTS IX_OrchestrationStep_Status ON OrchestrationStep(Status);");
            Execute("CREATE INDEX IF NOT EXISTS IX_OrchestrationMessage_TaskId_Sequence ON OrchestrationMessage(TaskId, Sequence);");
            Execute("CREATE INDEX IF NOT EXISTS IX_OrchestrationMessage_StepId ON OrchestrationMessage(StepId);");

            // ========================================
            // 阶段 5B 新增：Workflow Checkpoint 表
            // 用于 SDK ICheckpointManager 的 CommitCheckpointAsync / LookupCheckpointAsync 持久化。
            // 每个 paused 任务的 HITL 交互前后会产生 1 个 Checkpoint，宿主进程内重启时通过
            // RestoreCheckpointAsync 恢复（决策 5B-C：与 OrchestrationTask 同事务边界）。
            // 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §5B.2。
            // ========================================
            Execute("""
                CREATE TABLE IF NOT EXISTS WorkflowCheckpoints (
                    TaskId TEXT NOT NULL,
                    CheckpointId TEXT NOT NULL,
                    Payload BLOB NOT NULL,
                    CreatedAt INTEGER NOT NULL,
                    PRIMARY KEY (TaskId, CheckpointId),
                    FOREIGN KEY (TaskId) REFERENCES OrchestrationTask(Id) ON DELETE CASCADE
                );
                """);
            Execute("CREATE INDEX IF NOT EXISTS IX_WorkflowCheckpoints_TaskId_CreatedAt ON WorkflowCheckpoints(TaskId, CreatedAt DESC);");
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

            // v1.2.x: ChatMessages 结构化内容列，保存工具调用/结果等多态 AIContent 快照
            TryAddColumn("ALTER TABLE ChatMessages ADD COLUMN ContentsJson TEXT NOT NULL DEFAULT ''");

            // v1.3: ChatMessages 增加智能体来源字段，便于在消息层直接定位主/子智能体，
            // 同时保留智能体名称的历史快照（智能体重命名/删除后仍可追溯）。
            TryAddColumn("ALTER TABLE ChatMessages ADD COLUMN AgentId TEXT NOT NULL DEFAULT ''");
            TryAddColumn("ALTER TABLE ChatMessages ADD COLUMN AgentName TEXT NOT NULL DEFAULT ''");

            // 历史数据回填：通过 ChatSessions.AgentId 反查 Agents.Name，把消息上的 AgentId/AgentName
            // 一次性补齐。仅回填 AgentId 当前为空（IFNULL='')、且 SessionId 能匹配到会话的消息，避免重复覆盖。
            BackfillChatMessagesAgentInfo();

            // 阶段 6 Phase 2：任务级工具黑名单（决策 6-2-A 黑名单 + 6-2-B "pluginId:toolName" 粒度）。
            // 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 6 #1
            // 与 05-风险与规避.md §风险 7。
            // 默认 NULL（不过滤），保持向后兼容；旧任务行为不变。
            TryAddColumn("ALTER TABLE OrchestrationTask ADD COLUMN ToolBlacklistJson TEXT NULL");

            // 阶段 6 Phase 4：长期记忆 owner 控制（决策 6-4-A/B/C）。
            // 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 6 #5。
            // 默认 1（允许），保持向后兼容；旧 Agent + 新创建 Agent 都允许 Workflow 结果入长期记忆。
            // 0 时该 Agent 作为任务 Manager 完成任务后，
            // P4 TaskLifecycleEventArgs 完成事件中 Memory 插件检查该字段后跳过入库。
            TryAddColumn("ALTER TABLE Agents ADD COLUMN AllowWorkflowMemory INTEGER NOT NULL DEFAULT 1");
        }

        /// <summary>
        /// 一次性把 ChatMessages.AgentId / AgentName 从 ChatSessions + Agents 反查回填。
        /// 仅触达 AgentId 为空的旧记录，幂等可重复执行。
        /// </summary>
        private void BackfillChatMessagesAgentInfo()
        {
            try
            {
                Execute("""
                    UPDATE ChatMessages
                    SET AgentId = IFNULL((SELECT s.AgentId FROM ChatSessions s WHERE s.Id = ChatMessages.SessionId), ''),
                        AgentName = IFNULL((SELECT a.Name FROM ChatSessions s
                                             LEFT JOIN Agents a ON a.Id = s.AgentId
                                             WHERE s.Id = ChatMessages.SessionId), '')
                    WHERE IFNULL(AgentId, '') = '';
                    """);
            }
            catch (SqliteException)
            {
                // 任何老库结构异常都不阻断启动；后续插入仍可写入新列。
            }
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
            Execute("CREATE INDEX IF NOT EXISTS IX_ChatMessages_AgentId ON ChatMessages(AgentId);");
            Execute("CREATE INDEX IF NOT EXISTS IX_McpServers_IsEnabled ON McpServers(IsEnabled);");
            Execute("CREATE INDEX IF NOT EXISTS IX_SystemSettings_Group ON SystemSettings([Group]);");
            Execute("CREATE INDEX IF NOT EXISTS IX_SystemSettings_SortOrder ON SystemSettings(SortOrder);");
            Execute("CREATE INDEX IF NOT EXISTS IX_GlobalPlugins_IsEnabled ON GlobalPlugins(IsEnabled);");

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
            // 使用 exe 所在目录，而非进程工作目录（CurrentDirectory）。
            // 用 Start-Process / PowerShell 启动时 CurrentDirectory 是调用方目录，会导致找不到数据库。
            var folder = Path.GetDirectoryName(Environment.ProcessPath)
                         ?? Environment.CurrentDirectory;

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

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