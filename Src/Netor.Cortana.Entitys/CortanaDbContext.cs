using Microsoft.Data.Sqlite;

namespace Netor.Cortana.Entitys
{
    /// <summary>
    /// Cortana 的数据库上下文，基于 SQLite 的轻量级嵌入式关系数据库。
    /// 纯 P/Invoke 实现，AOT 安全，无反射依赖。
    /// </summary>
    /// <remarks>
    /// <para>数据库文件默认保存在应用当前目录下：</para>
    /// <para><c>madorin.db</c></para>
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

        /// <summary>
        /// 在事务中执行操作并返回结果。
        /// </summary>
        public T ExecuteInTransaction<T>(Func<SqliteConnection, SqliteTransaction, T> action)
        {
            lock (_lock)
            {
                using var transaction = _connection.BeginTransaction();
                try
                {
                    var result = action(_connection, transaction);
                    transaction.Commit();
                    return result;
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
        }

        /// <summary>
        /// 在 SQLite DEFERRED 事务中读取一致性快照。
        /// </summary>
        public T ExecuteInDeferredTransaction<T>(Func<SqliteConnection, SqliteTransaction, T> action)
        {
            lock (_lock)
            {
                using var transaction = _connection.BeginTransaction(deferred: true);
                try
                {
                    var result = action(_connection, transaction);
                    transaction.Commit();
                    return result;
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
                CREATE TABLE IF NOT EXISTS ChatSessions (
                    Id TEXT PRIMARY KEY,
                    CreatedTimestamp INTEGER NOT NULL,
                    UpdatedTimestamp INTEGER NOT NULL,
                    Categorize TEXT NOT NULL DEFAULT '',
                    Title TEXT NOT NULL DEFAULT '',
                    Summary TEXT NOT NULL DEFAULT '',
                    RawDiscription TEXT NOT NULL DEFAULT '',
                    AgentName TEXT NOT NULL DEFAULT '',
                    SourceTaskId TEXT NOT NULL DEFAULT '',
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

            EnsureDelegatedAgentJobTables();
            EnsureWorkModeTables();
            EnsureMeetingModeTables();
        }

        private void EnsureDelegatedAgentJobTables()
        {
            Execute("""
                CREATE TABLE IF NOT EXISTS DelegatedAgentJobs (
                    Id TEXT PRIMARY KEY,
                    ScopeKind TEXT NOT NULL DEFAULT 'chat',
                    ScopeId TEXT NOT NULL DEFAULT '',
                    ParentTurnId TEXT NOT NULL DEFAULT '',
                    ParentAgentId TEXT NOT NULL DEFAULT '',
                    ChildName TEXT NOT NULL DEFAULT '',
                    ChildInstructions TEXT NOT NULL DEFAULT '',
                    TaskInputJson TEXT NOT NULL DEFAULT '',
                    ToolMountsJson TEXT NOT NULL DEFAULT '[]',
                    ProviderId TEXT,
                    ModelId TEXT,
                    State TEXT NOT NULL DEFAULT 'pending',
                    ProgressDescription TEXT,
                    ResultJson TEXT,
                    Error TEXT,
                    CreatedAt INTEGER NOT NULL,
                    UpdatedAt INTEGER NOT NULL,
                    CompletedAt INTEGER
                );
                """);

            Execute("""
                CREATE TABLE IF NOT EXISTS DelegatedAgentJobLogs (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    JobId TEXT NOT NULL,
                    Kind TEXT NOT NULL DEFAULT '',
                    Message TEXT NOT NULL DEFAULT '',
                    CreatedAt INTEGER NOT NULL,
                    FOREIGN KEY (JobId) REFERENCES DelegatedAgentJobs(Id) ON DELETE CASCADE
                );
                """);
        }

        // ========================================
        // 工作模式（Work Mode）相关表
        // 详见 Docs/已完成功能规划/工作模式方案策划/10-数据模型与持久化设计.md
        // ========================================
        private void EnsureWorkModeTables()
        {
            // ─── WorkTasks：任务元数据 + RunId + IsActive + 长任务可靠性字段 ───
            Execute("""
                CREATE TABLE IF NOT EXISTS WorkTasks (
                    Id TEXT PRIMARY KEY,
                    SessionId TEXT NOT NULL,
                    WorkspaceId TEXT NOT NULL DEFAULT '',
                    Title TEXT NOT NULL DEFAULT '',
                    InitialInput TEXT NOT NULL DEFAULT '',
                    RunId TEXT,
                    ManagerRunId TEXT,
                    IsActive INTEGER NOT NULL DEFAULT 1,
                    CurrentPlanJson TEXT,
                    PendingRequestId TEXT,
                    PendingRequestKind TEXT,
                    PendingRequestData TEXT,
                    CompletedAt INTEGER,
                    FinalReport TEXT,
                    ErrorMessage TEXT,
                    Provider TEXT NOT NULL DEFAULT '',
                    Model TEXT NOT NULL DEFAULT '',
                    AgentName TEXT NOT NULL DEFAULT '',
                    MentionsJson TEXT,
                    SourceTaskId TEXT,
                    ParentTaskId TEXT,
                    IsOrphaned INTEGER NOT NULL DEFAULT 0,
                    OrphanedDetectedAt INTEGER,
                    HeartbeatAt INTEGER,
                    OrchestratorState TEXT,
                    OrchestratorHeartbeatAt INTEGER,
                    HasPreemption INTEGER NOT NULL DEFAULT 0,
                    CreatedAt INTEGER NOT NULL,
                    UpdatedAt INTEGER NOT NULL,
                    LastActiveAt INTEGER NOT NULL,
                    FOREIGN KEY (SessionId) REFERENCES ChatSessions(Id) ON DELETE CASCADE
                );
                """);

            // ─── WorkTaskEvents：B 层项目组长写给 A/UI 的事件队列 ───
            Execute("""
                CREATE TABLE IF NOT EXISTS WorkTaskEvents (
                    Id TEXT PRIMARY KEY,
                    TaskId TEXT NOT NULL,
                    RunId TEXT,
                    Kind TEXT NOT NULL,
                    Message TEXT NOT NULL,
                    CreatedAt INTEGER NOT NULL,
                    ReadAt INTEGER,
                    FOREIGN KEY (TaskId) REFERENCES WorkTasks(Id) ON DELETE CASCADE
                );
                """);

            // ─── WorkExecutionLogs：工具调用 / 验收 / 错误 / 自评等执行明细 ───
            Execute("""
                CREATE TABLE IF NOT EXISTS WorkExecutionLogs (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TaskId TEXT NOT NULL,
                    Sequence INTEGER NOT NULL,
                    LogType TEXT NOT NULL,
                    Content TEXT NOT NULL,
                    CreatedAt INTEGER NOT NULL,
                    FOREIGN KEY (TaskId) REFERENCES WorkTasks(Id) ON DELETE CASCADE
                );
                """);

            // ─── WorkPendingInputs：用户在执行中插话的输入队列 ───
            Execute("""
                CREATE TABLE IF NOT EXISTS WorkPendingInputs (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TaskId TEXT NOT NULL,
                    Content TEXT NOT NULL,
                    EnqueuedAt INTEGER NOT NULL,
                    Consumed INTEGER NOT NULL DEFAULT 0,
                    ConsumedAt INTEGER,
                    FOREIGN KEY (TaskId) REFERENCES WorkTasks(Id) ON DELETE CASCADE
                );
                """);

            // ─── WorkPlanTemplates：可复用的工作流模板 ───
            Execute("""
                CREATE TABLE IF NOT EXISTS WorkPlanTemplates (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Description TEXT NOT NULL DEFAULT '',
                    Category TEXT NOT NULL DEFAULT '',
                    PlanJson TEXT NOT NULL,
                    SourceTaskId TEXT,
                    SourceKind TEXT,
                    UseCount INTEGER NOT NULL DEFAULT 0,
                    LastUsedAt INTEGER,
                    Scope TEXT NOT NULL DEFAULT 'user',
                    WorkspaceId TEXT,
                    CreatedAt INTEGER NOT NULL,
                    UpdatedAt INTEGER NOT NULL
                );
                """);

            // ─── WorkBackgroundJobs：仅服务"主软件托管的背景任务"（v1.0 即子智能体）───
            // 不服务插件长任务（插件自治）
            Execute("""
                CREATE TABLE IF NOT EXISTS WorkBackgroundJobs (
                    Id TEXT PRIMARY KEY,
                    TaskId TEXT NOT NULL,
                    JobKind TEXT NOT NULL,
                    OwnerName TEXT,
                    State TEXT NOT NULL DEFAULT 'pending',
                    InputJson TEXT NOT NULL,
                    ProgressDescription TEXT,
                    ResultJson TEXT,
                    Error TEXT,
                    CreatedAt INTEGER NOT NULL,
                    UpdatedAt INTEGER NOT NULL,
                    CompletedAt INTEGER,
                    FOREIGN KEY (TaskId) REFERENCES WorkTasks(Id) ON DELETE CASCADE
                );
                """);

            // ─── WorkTaskContextMessages：A/C 的 Run 级上下文消息 ───
            // B 层不进上下文表，状态来源为 plan.yaml。
            Execute("""
                CREATE TABLE IF NOT EXISTS WorkTaskContextMessages (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TaskId TEXT NOT NULL,
                    RunId TEXT NOT NULL,
                    Sequence INTEGER NOT NULL,
                    Role TEXT NOT NULL DEFAULT '',
                    Content TEXT NOT NULL DEFAULT '',
                    CreatedAt INTEGER NOT NULL,
                    FOREIGN KEY (TaskId) REFERENCES WorkTasks(Id) ON DELETE CASCADE
                );
                """);

            // ─── WorkTaskContextSegments：A/C 的 Run 级压缩摘要段 ───
            Execute("""
                CREATE TABLE IF NOT EXISTS WorkTaskContextSegments (
                    Id TEXT PRIMARY KEY,
                    TaskId TEXT NOT NULL,
                    RunId TEXT NOT NULL,
                    SegmentIndex INTEGER NOT NULL DEFAULT 0,
                    StartSequence INTEGER NOT NULL DEFAULT 0,
                    EndSequence INTEGER NOT NULL DEFAULT 0,
                    Summary TEXT NOT NULL DEFAULT '',
                    OriginalMessageCount INTEGER NOT NULL DEFAULT 0,
                    ModelName TEXT NOT NULL DEFAULT '',
                    CreatedAt INTEGER NOT NULL,
                    UpdatedAt INTEGER NOT NULL,
                    FOREIGN KEY (TaskId) REFERENCES WorkTasks(Id) ON DELETE CASCADE
                );
                """);
        }

        // ========================================
        // 会议模式（Meeting Mode）相关表
        // 详见 Docs/已完成功能规划/会议模式方案策划/03-数据模型与持久化.md
        // ========================================
        private void EnsureMeetingModeTables()
        {
            Execute("""
                CREATE TABLE IF NOT EXISTS MeetingSessions (
                    Id TEXT PRIMARY KEY,
                    SessionId TEXT NOT NULL,
                    WorkspaceId TEXT NOT NULL DEFAULT '',
                    Topic TEXT NOT NULL,
                    HostAgentId TEXT NOT NULL DEFAULT 'system-meeting-host',
                    ParticipantsJson TEXT NOT NULL,
                    Status INTEGER NOT NULL DEFAULT 0,
                    RunId TEXT,
                    PendingRequestId TEXT,
                    PendingRequestKind TEXT,
                    PendingRequestData TEXT,
                    FinalSummaryMd TEXT,
                    Provider TEXT NOT NULL DEFAULT '',
                    Model TEXT NOT NULL DEFAULT '',
                    CreatedAt INTEGER NOT NULL,
                    UpdatedAt INTEGER NOT NULL,
                    EndedAt INTEGER,
                    FOREIGN KEY (SessionId) REFERENCES ChatSessions(Id) ON DELETE CASCADE
                );
                """);

            Execute("""
                CREATE TABLE IF NOT EXISTS MeetingMessages (
                    Id TEXT PRIMARY KEY,
                    MeetingId TEXT NOT NULL,
                    Sequence INTEGER NOT NULL,
                    SpeakerKind TEXT NOT NULL,
                    SpeakerId TEXT NOT NULL,
                    SpeakerName TEXT NOT NULL,
                    ContentMd TEXT NOT NULL DEFAULT '',
                    ThinkingMd TEXT,
                    ToolCallsJson TEXT,
                    AttachmentsJson TEXT,
                    MessageRole TEXT NOT NULL DEFAULT 'normal',
                    AwaitingUserReply INTEGER NOT NULL DEFAULT 0,
                    IsPartial INTEGER NOT NULL DEFAULT 0,
                    ErrorMessage TEXT,
                    CreatedAt INTEGER NOT NULL,
                    FOREIGN KEY (MeetingId) REFERENCES MeetingSessions(Id) ON DELETE CASCADE
                );
                """);

            Execute("""
                CREATE TABLE IF NOT EXISTS MeetingPendingInputs (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    MeetingId TEXT NOT NULL,
                    Content TEXT NOT NULL,
                    AttachmentsJson TEXT,
                    Kind TEXT NOT NULL DEFAULT 'interrupt',
                    EnqueuedAt INTEGER NOT NULL,
                    Consumed INTEGER NOT NULL DEFAULT 0,
                    ConsumedAt INTEGER,
                    FOREIGN KEY (MeetingId) REFERENCES MeetingSessions(Id) ON DELETE CASCADE
                );
                """);

            Execute("""
                CREATE TABLE IF NOT EXISTS MeetingAttachments (
                    Id TEXT PRIMARY KEY,
                    MeetingId TEXT NOT NULL,
                    FileName TEXT NOT NULL,
                    StoredPath TEXT NOT NULL,
                    MimeType TEXT NOT NULL DEFAULT '',
                    SizeBytes INTEGER NOT NULL DEFAULT 0,
                    UploadedAt INTEGER NOT NULL,
                    FOREIGN KEY (MeetingId) REFERENCES MeetingSessions(Id) ON DELETE CASCADE
                );
                """);

            Execute("""
                CREATE TABLE IF NOT EXISTS MeetingCompactionSegments (
                    Id TEXT PRIMARY KEY,
                    MeetingId TEXT NOT NULL,
                    SegmentIndex INTEGER NOT NULL,
                    StartSequence INTEGER NOT NULL,
                    EndSequence INTEGER NOT NULL,
                    Summary TEXT NOT NULL,
                    OriginalMessageCount INTEGER NOT NULL,
                    ModelName TEXT NOT NULL DEFAULT '',
                    CreatedAt INTEGER NOT NULL,
                    UpdatedAt INTEGER NOT NULL,
                    FOREIGN KEY (MeetingId) REFERENCES MeetingSessions(Id) ON DELETE CASCADE
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
            TryAddColumn("ALTER TABLE ChatSessions ADD COLUMN AgentName TEXT NOT NULL DEFAULT ''");
            TryAddColumn("ALTER TABLE ChatSessions ADD COLUMN SourceTaskId TEXT NOT NULL DEFAULT ''");
            TryAddColumn("ALTER TABLE WorkTasks ADD COLUMN AgentName TEXT NOT NULL DEFAULT ''");
            TryAddColumn("ALTER TABLE WorkTasks ADD COLUMN ManagerRunId TEXT NULL");
            TryAddColumn("ALTER TABLE WorkTasks ADD COLUMN OrchestratorState TEXT NULL");
            TryAddColumn("ALTER TABLE WorkTasks ADD COLUMN OrchestratorHeartbeatAt INTEGER NULL");
            // v1.2: AiModels 能力字段
            TryAddColumn("ALTER TABLE AiModels ADD COLUMN InputCapabilities INTEGER NOT NULL DEFAULT 1");
            TryAddColumn("ALTER TABLE AiModels ADD COLUMN OutputCapabilities INTEGER NOT NULL DEFAULT 1");
            TryAddColumn("ALTER TABLE AiModels ADD COLUMN InteractionCapabilities INTEGER NOT NULL DEFAULT 0");
            TryAddColumn("ALTER TABLE AiModels ADD COLUMN CapabilitySource TEXT NOT NULL DEFAULT 'manual'");
            TryAddColumn("ALTER TABLE AiModels ADD COLUMN CapabilityNotes TEXT NOT NULL DEFAULT ''");

            // v1.2.x: ChatMessages 结构化内容列，保存工具调用/结果等多态 AIContent 快照
            TryAddColumn("ALTER TABLE ChatMessages ADD COLUMN ContentsJson TEXT NOT NULL DEFAULT ''");

            // v1.3: ChatMessages 增加智能体来源字段，便于在消息层直接定位主/子智能体，
            // 同时保留智能体名称的历史快照（智能体重命名/删除后仍可追溯）。
            TryAddColumn("ALTER TABLE ChatMessages ADD COLUMN AgentId TEXT NOT NULL DEFAULT ''");
            TryAddColumn("ALTER TABLE ChatMessages ADD COLUMN AgentName TEXT NOT NULL DEFAULT ''");

            // 阶段 6 Phase 2：任务级工具黑名单（决策 6-2-A 黑名单 + 6-2-B "pluginId:toolName" 粒度）。
            // 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 6 #1
            // 与 05-风险与规避.md §风险 7。
            // 默认 NULL（不过滤），保持向后兼容；旧任务行为不变。
            TryAddColumn("ALTER TABLE OrchestrationTask ADD COLUMN ToolBlacklistJson TEXT NULL");

            ArchiveMeetingBackingChatSessions();
            LinkOrphanWorkModeChatSessions();
        }

        /// <summary>
        /// 历史版本会议模式曾通过普通 ChatSession 创建支撑会话，导致专家模式历史出现会议内容。
        /// 被 MeetingSessions 引用的 ChatSession 只服务会议外键与归档，不应参与专家模式会话列表。
        /// </summary>
        private void ArchiveMeetingBackingChatSessions()
        {
            try
            {
                Execute("""
                    UPDATE ChatSessions
                    SET IsArchived = 1
                    WHERE Id IN (SELECT SessionId FROM MeetingSessions);
                    """);
            }
            catch (SqliteException)
            {
                // 老库缺少会议表时跳过，后续建表后新会议会走专用支撑会话。
            }
        }

        /// <summary>
        /// 历史工作模式曾让主 Agent 走普通 ChatHistoryProvider，导致执行过程落入普通 ChatSessions。
        /// 这些会话没有 WorkTasks.SessionId 外键，但可通过同工作区、标题/初始输入和工作执行标记回连到 WorkTasks。
        /// </summary>
        private void LinkOrphanWorkModeChatSessions()
        {
            try
            {
                Execute("""
                    UPDATE ChatSessions
                    SET SourceTaskId = (
                        SELECT wt.Id
                        FROM WorkTasks wt
                        WHERE wt.WorkspaceId = ChatSessions.Categorize
                          AND (
                              (ChatSessions.Title <> '' AND instr(wt.InitialInput, ChatSessions.Title) > 0)
                              OR (ChatSessions.Title <> '' AND instr(ChatSessions.Title, wt.Title) > 0)
                              OR (ChatSessions.Title <> '' AND instr(wt.Title, ChatSessions.Title) > 0)
                          )
                        LIMIT 1
                    )
                    WHERE IFNULL(SourceTaskId, '') = ''
                      AND IsArchived = 0
                      AND NOT EXISTS (
                          SELECT 1 FROM WorkTasks linked
                          WHERE linked.SessionId = ChatSessions.Id
                      )
                      AND EXISTS (
                          SELECT 1 FROM ChatMessages cm
                          WHERE cm.SessionId = ChatSessions.Id
                            AND (
                                cm.Content LIKE '%步骤 [%'
                                OR cm.Content LIKE '%[工具调用]%'
                                OR cm.Content LIKE '%调用ID:%'
                                OR cm.Content LIKE '%verify_step%'
                                OR cm.Content LIKE '%sys_%'
                            )
                      )
                      AND EXISTS (
                          SELECT 1
                          FROM WorkTasks wt
                          WHERE wt.WorkspaceId = ChatSessions.Categorize
                            AND (
                                (ChatSessions.Title <> '' AND instr(wt.InitialInput, ChatSessions.Title) > 0)
                                OR (ChatSessions.Title <> '' AND instr(ChatSessions.Title, wt.Title) > 0)
                                OR (ChatSessions.Title <> '' AND instr(wt.Title, ChatSessions.Title) > 0)
                            )
                      );
                    """);
            }
            catch (SqliteException)
            {
                // 老库缺少工作模式表或消息表时跳过。
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
            Execute("CREATE INDEX IF NOT EXISTS IX_AiProviders_IsEnabled ON AiProviders(IsEnabled);");
            Execute("CREATE INDEX IF NOT EXISTS IX_ChatSessions_AgentName ON ChatSessions(AgentName);");
            Execute("CREATE INDEX IF NOT EXISTS IX_ChatSessions_SourceTaskId ON ChatSessions(SourceTaskId);");
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

            EnsureDelegatedAgentJobIndexes();
            EnsureWorkModeIndexes();
            EnsureMeetingModeIndexes();
        }

        private void EnsureDelegatedAgentJobIndexes()
        {
            Execute("CREATE INDEX IF NOT EXISTS IX_DelegatedAgentJobs_Scope_Created ON DelegatedAgentJobs(ScopeKind, ScopeId, CreatedAt DESC);");
            Execute("CREATE INDEX IF NOT EXISTS IX_DelegatedAgentJobs_State_Updated ON DelegatedAgentJobs(State, UpdatedAt DESC);");
            Execute("CREATE INDEX IF NOT EXISTS IX_DelegatedAgentJobLogs_JobId_Id ON DelegatedAgentJobLogs(JobId, Id);");
        }

        // ========================================
        // 工作模式相关索引
        // ========================================
        private void EnsureWorkModeIndexes()
        {
            // WorkTasks
            Execute("CREATE INDEX IF NOT EXISTS IX_WorkTasks_Session_Active ON WorkTasks(SessionId, IsActive, LastActiveAt DESC);");
            Execute("CREATE INDEX IF NOT EXISTS IX_WorkTasks_Workspace_Active ON WorkTasks(WorkspaceId, IsActive);");
            Execute("CREATE INDEX IF NOT EXISTS IX_WorkTasks_Source ON WorkTasks(SourceTaskId);");
            Execute("CREATE INDEX IF NOT EXISTS IX_WorkTasks_Parent ON WorkTasks(ParentTaskId);");
            Execute("CREATE INDEX IF NOT EXISTS IX_WorkTasks_Orphaned ON WorkTasks(IsOrphaned);");
            Execute("CREATE INDEX IF NOT EXISTS IX_WorkTasks_Orchestrator_Heartbeat ON WorkTasks(OrchestratorState, OrchestratorHeartbeatAt);");

            // WorkTaskEvents
            Execute("CREATE INDEX IF NOT EXISTS IX_WorkTaskEvents_Task_Read_Created ON WorkTaskEvents(TaskId, ReadAt, CreatedAt);");
            Execute("CREATE INDEX IF NOT EXISTS IX_WorkTaskEvents_Task_Run_Read ON WorkTaskEvents(TaskId, RunId, ReadAt);");

            // WorkExecutionLogs
            Execute("CREATE INDEX IF NOT EXISTS IX_WorkExecutionLogs_Task_Seq ON WorkExecutionLogs(TaskId, Sequence);");

            // WorkPendingInputs
            Execute("CREATE INDEX IF NOT EXISTS IX_WorkPendingInputs_Task_Pending ON WorkPendingInputs(TaskId, Consumed, EnqueuedAt);");

            // WorkPlanTemplates
            Execute("CREATE INDEX IF NOT EXISTS IX_WorkPlanTemplates_Scope ON WorkPlanTemplates(Scope);");
            Execute("CREATE INDEX IF NOT EXISTS IX_WorkPlanTemplates_Category ON WorkPlanTemplates(Category);");
            Execute("CREATE INDEX IF NOT EXISTS IX_WorkPlanTemplates_LastUsed ON WorkPlanTemplates(LastUsedAt DESC);");

            // WorkBackgroundJobs
            Execute("CREATE INDEX IF NOT EXISTS IX_WorkBackgroundJobs_Task_State ON WorkBackgroundJobs(TaskId, State);");
            Execute("CREATE INDEX IF NOT EXISTS IX_WorkBackgroundJobs_Kind ON WorkBackgroundJobs(JobKind);");

            // WorkTaskContext
            Execute("CREATE INDEX IF NOT EXISTS IX_WorkTaskContextMessages_Task_Run_Seq ON WorkTaskContextMessages(TaskId, RunId, Sequence);");
            Execute("CREATE INDEX IF NOT EXISTS IX_WorkTaskContextSegments_Task_Run_Index ON WorkTaskContextSegments(TaskId, RunId, SegmentIndex);");
        }

        // ========================================
        // 会议模式相关索引
        // ========================================
        private void EnsureMeetingModeIndexes()
        {
            Execute("CREATE INDEX IF NOT EXISTS idx_meeting_session ON MeetingSessions(SessionId);");
            Execute("CREATE INDEX IF NOT EXISTS idx_meeting_status ON MeetingSessions(Status, UpdatedAt DESC);");
            Execute("""
                CREATE UNIQUE INDEX IF NOT EXISTS idx_meeting_active_unique
                    ON MeetingSessions(SessionId)
                    WHERE Status IN (0, 1);
                """);

            Execute("CREATE INDEX IF NOT EXISTS idx_meeting_msg ON MeetingMessages(MeetingId, Sequence);");
            Execute("""
                CREATE INDEX IF NOT EXISTS idx_meeting_msg_summary
                    ON MeetingMessages(MeetingId, Sequence DESC)
                    WHERE MessageRole = 'summary';
                """);
            Execute("CREATE UNIQUE INDEX IF NOT EXISTS uk_meeting_msg_sequence ON MeetingMessages(MeetingId, Sequence);");

            Execute("CREATE INDEX IF NOT EXISTS idx_meeting_pending ON MeetingPendingInputs(MeetingId, Consumed, EnqueuedAt);");
            Execute("CREATE INDEX IF NOT EXISTS idx_meeting_attach ON MeetingAttachments(MeetingId);");
            Execute("CREATE INDEX IF NOT EXISTS idx_meeting_compact ON MeetingCompactionSegments(MeetingId, SegmentIndex);");
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

            return Path.Combine(folder, "madorin.db");
        }

        /// <summary>
        /// 获取旧版数据库文件路径，用于首次升级时选择性导入用户配置。
        /// </summary>
        public static string GetLegacyDbPath()
        {
            var folder = Path.GetDirectoryName(Environment.ProcessPath)
                         ?? Environment.CurrentDirectory;

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
