using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.Memory.Storage;

public sealed class MemoryStore(
    IMemoryDatabase database,
    ObservationRecordsTable observationRecords,
    MemoryFragmentsTable memoryFragments,
    MemoryAbstractionsTable memoryAbstractions,
    MemoryLinksTable memoryLinks,
    MemoryEventsTable memoryEvents,
    RecallLogsTable recallLogs,
    MemoryMutationsTable memoryMutations,
    MemorySettingsTable memorySettings,
    MemoryProcessingStatesTable processingStates,
    ILogger<MemoryStore> logger) : IMemoryStore
{
    public void EnsureInitialized()
    {
        using var conn = database.OpenConnection();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS observation_records (
  id TEXT PRIMARY KEY,
  agentId TEXT,
  workspaceId TEXT,
  sessionId TEXT NOT NULL,
  turnId TEXT,
  messageId TEXT,
  eventType TEXT,
  role TEXT NOT NULL,
  content TEXT NULL,
  attachments TEXT NOT NULL DEFAULT '[]',
  createdTimestamp INTEGER NOT NULL,
  modelName TEXT NULL,
  traceId TEXT,
  sourceFacts TEXT NOT NULL DEFAULT '{}',
  schemaVersion INTEGER NOT NULL DEFAULT 2,
  recordVersion INTEGER NOT NULL DEFAULT 1,
  createdAt TEXT NOT NULL DEFAULT ''
);";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS schema_migrations (
  version INTEGER PRIMARY KEY,
  appliedAt TEXT NOT NULL,
  description TEXT NOT NULL,
  isRollbackSafe INTEGER NOT NULL
);";
            cmd.ExecuteNonQuery();
        }

        void TryAddColumn(string sql)
        {
            try { using var c = conn.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
            catch (SqliteException) { }
        }

        TryAddColumn("ALTER TABLE observation_records ADD COLUMN agentId TEXT");
        TryAddColumn("ALTER TABLE observation_records ADD COLUMN workspaceId TEXT");
        TryAddColumn("ALTER TABLE observation_records ADD COLUMN turnId TEXT");
        TryAddColumn("ALTER TABLE observation_records ADD COLUMN messageId TEXT");
        TryAddColumn("ALTER TABLE observation_records ADD COLUMN eventType TEXT");
        TryAddColumn("ALTER TABLE observation_records ADD COLUMN attachments TEXT NOT NULL DEFAULT '[]'");
        TryAddColumn("ALTER TABLE observation_records ADD COLUMN traceId TEXT");
        TryAddColumn("ALTER TABLE observation_records ADD COLUMN sourceFacts TEXT NOT NULL DEFAULT '{}'");
        TryAddColumn("ALTER TABLE observation_records ADD COLUMN schemaVersion INTEGER NOT NULL DEFAULT 2");
        TryAddColumn("ALTER TABLE observation_records ADD COLUMN recordVersion INTEGER NOT NULL DEFAULT 1");
        TryAddColumn("ALTER TABLE observation_records ADD COLUMN createdAt TEXT NOT NULL DEFAULT ''");

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS memory_fragments (
  id TEXT PRIMARY KEY,
  agentId TEXT NOT NULL,
  workspaceId TEXT NULL,
  memoryType TEXT NOT NULL,
  topic TEXT NOT NULL,
  title TEXT NULL,
  summary TEXT NOT NULL,
  detail TEXT NULL,
  keywordsJson TEXT NULL,
  tagsJson TEXT NULL,
  entitiesJson TEXT NULL,
  sourceObservationIdsJson TEXT NOT NULL,
  sourceSessionIdsJson TEXT NULL,
  sourceTurnIdsJson TEXT NULL,
  importance REAL NOT NULL,
  confidence REAL NOT NULL,
  emotionalWeight REAL NOT NULL DEFAULT 0,
  novelty REAL NOT NULL DEFAULT 0,
  salienceScore REAL NOT NULL,
  retentionScore REAL NOT NULL,
  decayRate REAL NOT NULL,
  accessCount INTEGER NOT NULL DEFAULT 0,
  reinforcementCount INTEGER NOT NULL DEFAULT 0,
  contradictionCount INTEGER NOT NULL DEFAULT 0,
  clarityLevel TEXT NOT NULL DEFAULT 'blurred',
  confirmationState TEXT NOT NULL DEFAULT 'pending',
  lifecycleState TEXT NOT NULL DEFAULT 'candidate',
  lastAccessedAt TEXT NULL,
  lastReinforcedAt TEXT NULL,
  expiresAt TEXT NULL,
  schemaVersion INTEGER NOT NULL DEFAULT 2,
  recordVersion INTEGER NOT NULL DEFAULT 1,
  compatibilityTagsJson TEXT NULL,
  createdAt TEXT NOT NULL,
  updatedAt TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS memory_abstractions (
  id TEXT PRIMARY KEY,
  agentId TEXT NOT NULL,
  workspaceId TEXT NULL,
  abstractionType TEXT NOT NULL,
  title TEXT NULL,
  statement TEXT NOT NULL,
  summary TEXT NOT NULL,
  supportingMemoryIdsJson TEXT NOT NULL,
  counterMemoryIdsJson TEXT NULL,
  keywordsJson TEXT NULL,
  tagsJson TEXT NULL,
  importance REAL NOT NULL,
  confidence REAL NOT NULL,
  stabilityScore REAL NOT NULL,
  retentionScore REAL NOT NULL,
  decayRate REAL NOT NULL,
  accessCount INTEGER NOT NULL DEFAULT 0,
  reinforcementCount INTEGER NOT NULL DEFAULT 0,
  contradictionCount INTEGER NOT NULL DEFAULT 0,
  clarityLevel TEXT NOT NULL DEFAULT 'blurred',
  confirmationState TEXT NOT NULL DEFAULT 'pending',
  lifecycleState TEXT NOT NULL DEFAULT 'candidate',
  lastValidatedAt TEXT NULL,
  lastAccessedAt TEXT NULL,
  expiresAt TEXT NULL,
  schemaVersion INTEGER NOT NULL DEFAULT 2,
  recordVersion INTEGER NOT NULL DEFAULT 1,
  compatibilityTagsJson TEXT NULL,
  createdAt TEXT NOT NULL,
  updatedAt TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS memory_links (
  id TEXT PRIMARY KEY,
  agentId TEXT NOT NULL,
  sourceMemoryId TEXT NOT NULL,
  sourceMemoryKind TEXT NOT NULL,
  targetMemoryId TEXT NOT NULL,
  targetMemoryKind TEXT NOT NULL,
  relationType TEXT NOT NULL,
  weight REAL NOT NULL,
  evidenceCount INTEGER NOT NULL,
  confidence REAL NOT NULL,
  schemaVersion INTEGER NOT NULL DEFAULT 2,
  recordVersion INTEGER NOT NULL DEFAULT 1,
  createdAt TEXT NOT NULL,
  updatedAt TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS memory_events (
  eventId TEXT PRIMARY KEY,
  agentId TEXT NOT NULL,
  eventType TEXT NOT NULL,
  payloadJson TEXT NOT NULL,
  processedAt TEXT NULL,
  schemaVersion INTEGER NOT NULL DEFAULT 2,
  recordVersion INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS recall_logs (
  id TEXT PRIMARY KEY,
  requestId TEXT NOT NULL,
  agentId TEXT NOT NULL,
  workspaceId TEXT NULL,
  queryText TEXT NULL,
  queryIntent TEXT NULL,
  triggerSource TEXT NULL,
  hitMemoryIdsJson TEXT NOT NULL DEFAULT '[]',
  supportingMemoryIdsJson TEXT NOT NULL DEFAULT '[]',
  suppressedMemoryIdsJson TEXT NOT NULL DEFAULT '[]',
  recallSummary TEXT NULL,
  confidence REAL NOT NULL DEFAULT 0,
  budgetJson TEXT NULL,
  appliedPolicyJson TEXT NULL,
  traceId TEXT NULL,
  schemaVersion INTEGER NOT NULL DEFAULT 2,
  recordVersion INTEGER NOT NULL DEFAULT 1,
  createdAt TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS memory_mutations (
  id TEXT PRIMARY KEY,
  agentId TEXT NOT NULL,
  memoryId TEXT NOT NULL,
  memoryKind TEXT NOT NULL,
  mutationType TEXT NOT NULL,
  beforeJson TEXT NULL,
  afterJson TEXT NULL,
  reason TEXT NULL,
  traceId TEXT NULL,
  schemaVersion INTEGER NOT NULL DEFAULT 2,
  recordVersion INTEGER NOT NULL DEFAULT 1,
  createdAt TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS memory_settings (
  id TEXT PRIMARY KEY,
  agentId TEXT NULL,
  workspaceId TEXT NULL,
  settingKey TEXT NOT NULL,
  settingValue TEXT NOT NULL,
  valueType TEXT NOT NULL,
  category TEXT NOT NULL,
  description TEXT NULL,
  isEnabled INTEGER NOT NULL DEFAULT 1,
  schemaVersion INTEGER NOT NULL DEFAULT 3,
  recordVersion INTEGER NOT NULL DEFAULT 1,
  createdAt TEXT NOT NULL,
  updatedAt TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS memory_processing_states (
  id TEXT PRIMARY KEY,
  processorName TEXT NOT NULL,
  agentId TEXT NULL,
  workspaceId TEXT NULL,
  state TEXT NOT NULL,
  lastObservationTimestamp INTEGER NOT NULL DEFAULT 0,
  lastObservationId TEXT NULL,
  processedCount INTEGER NOT NULL DEFAULT 0,
  createdFragmentCount INTEGER NOT NULL DEFAULT 0,
  mergedFragmentCount INTEGER NOT NULL DEFAULT 0,
  createdAbstractionCount INTEGER NOT NULL DEFAULT 0,
  lastError TEXT NULL,
  lockedUntil TEXT NULL,
  createdAt TEXT NOT NULL,
  updatedAt TEXT NOT NULL
);";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"CREATE INDEX IF NOT EXISTS IX_obs_session_time ON observation_records(sessionId, createdTimestamp);
CREATE INDEX IF NOT EXISTS IX_obs_messageId ON observation_records(messageId);
CREATE INDEX IF NOT EXISTS IX_obs_eventType ON observation_records(eventType);
CREATE INDEX IF NOT EXISTS IX_obs_agent_session_turn ON observation_records(agentId, sessionId, turnId);
CREATE INDEX IF NOT EXISTS IX_obs_trace ON observation_records(traceId);
CREATE INDEX IF NOT EXISTS IX_fragment_agent_topic_score ON memory_fragments(agentId, topic, salienceScore DESC);
CREATE INDEX IF NOT EXISTS IX_fragment_agent_workspace ON memory_fragments(agentId, workspaceId);
CREATE INDEX IF NOT EXISTS IX_fragment_state_confirm ON memory_fragments(lifecycleState, confirmationState);
CREATE INDEX IF NOT EXISTS IX_fragment_updated ON memory_fragments(updatedAt);
CREATE INDEX IF NOT EXISTS IX_abstraction_agent_type ON memory_abstractions(agentId, abstractionType);
CREATE INDEX IF NOT EXISTS IX_abstraction_state_confirm ON memory_abstractions(lifecycleState, confirmationState);
CREATE INDEX IF NOT EXISTS IX_link_source ON memory_links(sourceMemoryId, sourceMemoryKind);
CREATE INDEX IF NOT EXISTS IX_link_target ON memory_links(targetMemoryId, targetMemoryKind);
CREATE INDEX IF NOT EXISTS IX_link_relation ON memory_links(agentId, relationType);
CREATE INDEX IF NOT EXISTS IX_event_agent_type ON memory_events(agentId, eventType);
CREATE INDEX IF NOT EXISTS IX_recall_agent_created ON recall_logs(agentId, createdAt);
CREATE INDEX IF NOT EXISTS IX_mutation_memory ON memory_mutations(memoryId, memoryKind);
CREATE UNIQUE INDEX IF NOT EXISTS IX_setting_scope_key ON memory_settings(agentId, workspaceId, settingKey);
CREATE INDEX IF NOT EXISTS IX_setting_scope_key_normalized ON memory_settings(ifnull(agentId, ''), ifnull(workspaceId, ''), settingKey);
CREATE INDEX IF NOT EXISTS IX_setting_category ON memory_settings(category, isEnabled);
CREATE UNIQUE INDEX IF NOT EXISTS IX_processing_scope ON memory_processing_states(processorName, ifnull(agentId, ''), ifnull(workspaceId, ''));
CREATE INDEX IF NOT EXISTS IX_processing_state ON memory_processing_states(state, updatedAt);";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"INSERT OR IGNORE INTO schema_migrations (version, appliedAt, description, isRollbackSafe)
VALUES (1, datetime('now'), 'Initial memory database baseline', 1);
INSERT OR IGNORE INTO schema_migrations (version, appliedAt, description, isRollbackSafe)
VALUES (2, datetime('now'), 'Expand memory database schema for long-term memory', 1);
INSERT OR IGNORE INTO schema_migrations (version, appliedAt, description, isRollbackSafe)
VALUES (3, datetime('now'), 'Add memory system settings', 1);";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"INSERT OR IGNORE INTO memory_settings (id, agentId, workspaceId, settingKey, settingValue, valueType, category, description, isEnabled, schemaVersion, recordVersion, createdAt, updatedAt)
VALUES
('global.decay.enabled', NULL, NULL, 'decay.enabled', 'true', 'bool', 'decay', '是否启用记忆衰减。', 1, 3, 1, datetime('now'), datetime('now')),
('global.decay.defaultRate', NULL, NULL, 'decay.defaultRate', '0.015', 'double', 'decay', '默认记忆衰减率。', 1, 3, 1, datetime('now'), datetime('now')),
('global.decay.scanIntervalMinutes', NULL, NULL, 'decay.scanIntervalMinutes', '60', 'int', 'decay', '记忆衰减扫描间隔，单位分钟。', 1, 3, 1, datetime('now'), datetime('now')),
('global.retention.minimumScore', NULL, NULL, 'retention.minimumScore', '0.2', 'double', 'retention', '参与保留计算的最低保留分数。', 1, 3, 1, datetime('now'), datetime('now')),
('global.retention.forgetThreshold', NULL, NULL, 'retention.forgetThreshold', '0.05', 'double', 'retention', '低于该阈值的记忆可进入遗忘候选。', 1, 3, 1, datetime('now'), datetime('now')),
('global.recall.maxWindowCount', NULL, NULL, 'recall.maxWindowCount', '6', 'int', 'recall', '每次向上层提供的最大记忆窗口数量。', 1, 3, 1, datetime('now'), datetime('now')),
('global.recall.maxMemoryCount', NULL, NULL, 'recall.maxMemoryCount', '20', 'int', 'recall', '每次召回最多返回的记忆数量。', 1, 3, 1, datetime('now'), datetime('now')),
('global.recall.minimumConfidence', NULL, NULL, 'recall.minimumConfidence', '0.35', 'double', 'recall', '允许参与召回的最低记忆可信度。', 1, 3, 1, datetime('now'), datetime('now')),
('global.recall.includeCandidateMemories', NULL, NULL, 'recall.includeCandidateMemories', 'false', 'bool', 'recall', '是否允许候选记忆参与召回。', 1, 3, 1, datetime('now'), datetime('now')),
('global.supply.enabled', NULL, NULL, 'supply.enabled', 'true', 'bool', 'supply', '是否启用主动记忆供应。', 1, 3, 1, datetime('now'), datetime('now')),
('global.supply.maxMemoryCount', NULL, NULL, 'supply.maxMemoryCount', '8', 'int', 'supply', '主动供应给上层的最大记忆数量。', 1, 3, 1, datetime('now'), datetime('now')),
('global.abstraction.enabled', NULL, NULL, 'abstraction.enabled', 'true', 'bool', 'abstraction', '是否启用自动抽象记忆生成。', 1, 3, 1, datetime('now'), datetime('now')),
('global.abstraction.minimumSupportCount', NULL, NULL, 'abstraction.minimumSupportCount', '3', 'int', 'abstraction', '生成抽象记忆需要的最小支撑记忆数。', 1, 3, 1, datetime('now'), datetime('now')),
('global.abstraction.minimumConfidence', NULL, NULL, 'abstraction.minimumConfidence', '0.55', 'double', 'abstraction', '抽象记忆入库所需的最低可信度。', 1, 3, 1, datetime('now'), datetime('now')),
('global.governance.auditEnabled', NULL, NULL, 'governance.auditEnabled', 'true', 'bool', 'governance', '是否启用记忆治理审计。', 1, 3, 1, datetime('now'), datetime('now'));";
            cmd.ExecuteNonQuery();
        }

        logger.LogInformation("MemoryStore 初始化完成：{Path}", database.DatabasePath);
    }

    public void InsertObservation(ObservationRecord r)
    {
        try
        {
            observationRecords.Insert(r);
        }
        catch (SqliteException ex)
        {
            throw CreateStorageException("插入观察记录", ex);
        }
    }

    public void BulkInsertObservations(IEnumerable<ObservationRecord> records)
    {
        try
        {
            observationRecords.BulkInsert(records);
        }
        catch (SqliteException ex)
        {
            throw CreateStorageException("批量插入观察记录", ex);
        }
    }

    public void UpsertMemoryFragment(MemoryFragment fragment)
    {
        try
        {
            memoryFragments.Upsert(fragment);
        }
        catch (SqliteException ex)
        {
            throw CreateStorageException("保存记忆片段", ex);
        }
    }

    public void UpsertMemoryAbstraction(MemoryAbstraction abstraction)
    {
        try
        {
            memoryAbstractions.Upsert(abstraction);
        }
        catch (SqliteException ex)
        {
            throw CreateStorageException("保存抽象记忆", ex);
        }
    }

    public void UpsertMemoryLink(MemoryLink link)
    {
        try
        {
            memoryLinks.Upsert(link);
        }
        catch (SqliteException ex)
        {
            throw CreateStorageException("保存记忆关系", ex);
        }
    }

    public void InsertMemoryEvent(MemoryEvent memoryEvent)
    {
        try
        {
            memoryEvents.Insert(memoryEvent);
        }
        catch (SqliteException ex)
        {
            throw CreateStorageException("插入记忆事件", ex);
        }
    }

    public void InsertRecallLog(RecallLog recallLog)
    {
        try
        {
            recallLogs.Insert(recallLog);
        }
        catch (SqliteException ex)
        {
            throw CreateStorageException("插入召回日志", ex);
        }
    }

    public void InsertMemoryMutation(MemoryMutation mutation)
    {
        try
        {
            memoryMutations.Insert(mutation);
        }
        catch (SqliteException ex)
        {
            throw CreateStorageException("插入记忆变更记录", ex);
        }
    }

    public void AddManualMemory(MemoryFragment fragment, MemoryMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        ArgumentNullException.ThrowIfNull(mutation);

        try
        {
            memoryFragments.Upsert(fragment);
            memoryMutations.Insert(mutation);
        }
        catch (SqliteException ex)
        {
            throw CreateStorageException("写入人工记忆", ex);
        }
    }

    public void UpsertMemorySetting(MemorySetting setting)
    {
        try
        {
            memorySettings.Upsert(setting);
        }
        catch (SqliteException ex)
        {
            throw CreateStorageException("保存记忆配置", ex);
        }
    }

    public IReadOnlyList<MemorySetting> GetMemorySettings(string? agentId, string? workspaceId)
    {
        try
        {
            return memorySettings.GetEffective(agentId, workspaceId);
        }
        catch (SqliteException ex)
        {
            throw CreateStorageException("读取记忆配置", ex);
        }
    }

    public IReadOnlyList<string> GetDistinctAgentIds()
    {
        try
        {
            return memoryFragments.GetDistinctAgentIds();
        }
        catch (SqliteException ex)
        {
            throw CreateStorageException("读取智能体列表", ex);
        }
    }

    public IReadOnlyList<ObservationRecord> GetUnprocessedObservations(string processorName, string? agentId, string? workspaceId, int limit)
    {
        if (string.IsNullOrWhiteSpace(processorName)) throw new ArgumentException("处理器名称不能为空。", nameof(processorName));
        if (limit <= 0) return [];

        try
        {
            var state = GetProcessingState(processorName, agentId, workspaceId);
            return observationRecords.GetUnprocessed(agentId, workspaceId, state.LastObservationTimestamp, state.LastObservationId, limit);
        }
        catch (SqliteException ex)
        {
            throw CreateStorageException("读取待处理观察记录", ex);
        }
    }

    public MemoryProcessingState GetProcessingState(string processorName, string? agentId, string? workspaceId)
    {
        try
        {
            return processingStates.Get(processorName, agentId, workspaceId);
        }
        catch (SqliteException ex)
        {
            throw CreateStorageException("读取处理状态", ex);
        }
    }

    public void UpsertProcessingState(MemoryProcessingState state)
    {
        try
        {
            processingStates.Upsert(state);
        }
        catch (SqliteException ex)
        {
            throw CreateStorageException("保存处理状态", ex);
        }
    }

    public IReadOnlyList<MemoryFragment> SearchSimilarFragments(string agentId, string? workspaceId, string memoryType, string summary, int limit)
    {
        try
        {
            return memoryFragments.SearchSimilar(agentId, workspaceId, memoryType, summary, limit);
        }
        catch (SqliteException ex)
        {
            throw CreateStorageException("检索相似记忆片段", ex);
        }
    }

    public IReadOnlyList<MemoryFragment> GetFragmentsForAbstraction(string agentId, string? workspaceId, string? topic, int minSupportCount, int limit)
    {
        _ = minSupportCount;
        try
        {
            return memoryFragments.GetForAbstraction(agentId, workspaceId, topic, limit);
        }
        catch (SqliteException ex)
        {
            throw CreateStorageException("读取抽象候选记忆片段", ex);
        }
    }

    public IReadOnlyList<MemoryRecallItem> SearchRecallCandidates(string agentId, string? workspaceId, string? queryText, double minimumConfidence, bool includeCandidateMemories, int limit)
    {
        if (string.IsNullOrWhiteSpace(agentId)) throw new ArgumentException("智能体标识不能为空。", nameof(agentId));
        if (limit <= 0) return [];
        var queryTerms = GetQueryTerms(queryText);
        var hasQuery = queryTerms.Count > 0;
        var candidateLimit = hasQuery ? Math.Max(limit * 50, 500) : Math.Max(limit * 5, limit);
        var fragmentTextFilter = BuildRecallTextFilter(queryTerms, "topic", "title", "summary", "detail");
        var abstractionTextFilter = BuildRecallTextFilter(queryTerms, "abstractionType", "title", "summary", "statement");

        try
        {
            using var conn = database.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT * FROM (
  SELECT id, 'fragment' AS kind, agentId, workspaceId, topic, title, summary, detail, confidence, salienceScore, retentionScore, accessCount, lifecycleState, confirmationState, updatedAt,
         (confidence * 0.35 + salienceScore * 0.30 + retentionScore * 0.25 + min(accessCount, 10) * 0.01) AS baseScore
  FROM memory_fragments
  WHERE agentId = @agent
    AND (@workspace IS NULL OR workspaceId IS NULL OR workspaceId = @workspace)
    AND confidence >= @minimumConfidence
    AND lifecycleState <> 'forgotten'
    AND confirmationState <> 'rejected'
    AND (" + fragmentTextFilter + @")
    AND (@includeCandidates = 1 OR (lifecycleState = 'active' AND confirmationState = 'confirmed') OR (@hasQuery = 1 AND lifecycleState = 'candidate' AND confirmationState = 'pending'))
  UNION ALL
  SELECT id, 'abstraction' AS kind, agentId, workspaceId, abstractionType AS topic, title, summary, statement AS detail, confidence, stabilityScore AS salienceScore, retentionScore, accessCount, lifecycleState, confirmationState, updatedAt,
         (confidence * 0.35 + stabilityScore * 0.30 + retentionScore * 0.25 + min(accessCount, 10) * 0.01) AS baseScore
  FROM memory_abstractions
  WHERE agentId = @agent
    AND (@workspace IS NULL OR workspaceId IS NULL OR workspaceId = @workspace)
    AND confidence >= @minimumConfidence
    AND lifecycleState <> 'forgotten'
    AND confirmationState <> 'rejected'
    AND (" + abstractionTextFilter + @")
    AND (@includeCandidates = 1 OR (lifecycleState = 'active' AND confirmationState = 'confirmed') OR (@hasQuery = 1 AND lifecycleState = 'candidate' AND confirmationState = 'pending'))
)
ORDER BY baseScore DESC
LIMIT @limit";
            Set(cmd, "@agent", agentId);
            SetNullable(cmd, "@workspace", workspaceId);
            Set(cmd, "@minimumConfidence", minimumConfidence);
            Set(cmd, "@includeCandidates", includeCandidateMemories ? 1 : 0);
            Set(cmd, "@hasQuery", hasQuery ? 1 : 0);
            Set(cmd, "@limit", candidateLimit);
            for (var i = 0; i < queryTerms.Count; i++)
            {
                Set(cmd, $"@term{i}", $"%{EscapeLikeTerm(queryTerms[i])}%");
            }

            var items = new List<MemoryRecallItem>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(ReadRecallItem(reader, queryText));
            }

            var matchedItems = hasQuery
                ? items.Where(item => HasTextMatch(queryText, item.Topic, item.Title, item.Summary, item.Detail))
                : items;

            return matchedItems
                .OrderByDescending(static item => item.RecallScore)
                .Take(limit)
                .ToList();
        }
        catch (SqliteException ex)
        {
            throw CreateStorageException("检索召回候选记忆", ex);
        }
    }

    public void RecordMemoryAccesses(IEnumerable<MemoryRecallItem> items, string accessedAt)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (string.IsNullOrWhiteSpace(accessedAt)) throw new ArgumentException("访问时间不能为空。", nameof(accessedAt));

        var list = items.ToList();
        if (list.Count == 0) return;

        try
        {
            foreach (var item in list)
            {
                switch (item.Kind)
                {
                    case "fragment":
                        memoryFragments.RecordAccess(item.Id, accessedAt);
                        break;
                    case "abstraction":
                        memoryAbstractions.RecordAccess(item.Id, accessedAt);
                        break;
                }
            }
        }
        catch (SqliteException ex)
        {
            throw CreateStorageException("记录记忆访问", ex);
        }
    }

    public IReadOnlyList<MemoryRecentItem> ListRecentMemories(string? agentId, string? workspaceId, string? kind, int limit)
    {
        if (limit <= 0) return [];

        var normalizedKind = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim().ToLowerInvariant();
        if (normalizedKind is not null and not "fragment" and not "abstraction")
            throw new ArgumentException("记忆类别只支持 fragment、abstraction 或空值。", nameof(kind));

        try
        {
            var fragments = normalizedKind == "abstraction"
                ? Enumerable.Empty<MemoryRecentItem>()
                : memoryFragments.List(agentId, workspaceId, limit).Select(static fragment => new MemoryRecentItem
                {
                    Id = fragment.Id,
                    Kind = "fragment",
                    Topic = fragment.Topic,
                    Title = fragment.Title,
                    Summary = fragment.Summary,
                    Confidence = fragment.Confidence,
                    LifecycleState = fragment.LifecycleState,
                    ConfirmationState = fragment.ConfirmationState,
                    UpdatedAt = fragment.UpdatedAt
                });

            var abstractions = normalizedKind == "fragment"
                ? Enumerable.Empty<MemoryRecentItem>()
                : memoryAbstractions.List(agentId, workspaceId, limit).Select(static abstraction => new MemoryRecentItem
                {
                    Id = abstraction.Id,
                    Kind = "abstraction",
                    Topic = abstraction.AbstractionType,
                    Title = abstraction.Title,
                    Summary = abstraction.Summary,
                    Confidence = abstraction.Confidence,
                    LifecycleState = abstraction.LifecycleState,
                    ConfirmationState = abstraction.ConfirmationState,
                    UpdatedAt = abstraction.UpdatedAt
                });

            return fragments
                .Concat(abstractions)
                .OrderByDescending(static item => item.UpdatedAt)
                .Take(limit)
                .ToList();
        }
        catch (SqliteException ex)
        {
            throw CreateStorageException("读取最近记忆", ex);
        }
    }

    public MemoryStoreStatusSnapshot GetStatusSnapshot(string? agentId, string? workspaceId)
    {
        try
        {
            using var conn = database.OpenConnection();
            return new MemoryStoreStatusSnapshot
            {
                ObservationCount = CountRows(conn, "observation_records", agentId, workspaceId, false),
                FragmentCount = CountRows(conn, "memory_fragments", agentId, workspaceId, true),
                AbstractionCount = CountRows(conn, "memory_abstractions", agentId, workspaceId, true),
                RecallLogCount = CountRows(conn, "recall_logs", agentId, workspaceId, true)
            };
        }
        catch (SqliteException ex)
        {
            throw CreateStorageException("读取记忆系统状态", ex);
        }
    }

    private const string InsertObservationSql = "INSERT OR IGNORE INTO observation_records (id, agentId, workspaceId, sessionId, turnId, messageId, eventType, role, content, attachments, createdTimestamp, modelName, traceId, sourceFacts, schemaVersion, recordVersion, createdAt) VALUES (@id,@agent,@workspace,@sid,@turn,@mid,@etype,@role,@content,@atts,@ts,@model,@trace,@facts,@schema,@record,@created)";

    private static int CountRows(SqliteConnection connection, string tableName, string? agentId, string? workspaceId, bool requireAgentFilter)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $@"SELECT COUNT(*) FROM {tableName}
WHERE (@agent IS NULL OR agentId = @agent)
  AND (@workspace IS NULL OR workspaceId IS NULL OR workspaceId = @workspace)
  AND (@agentRequired = 0 OR @agent IS NOT NULL OR agentId IS NOT NULL)";
        SetNullable(command, "@agent", agentId);
        SetNullable(command, "@workspace", workspaceId);
        Set(command, "@agentRequired", requireAgentFilter ? 1 : 0);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void Prepare(SqliteCommand cmd)
    {
        cmd.Parameters.Clear();
        cmd.Parameters.AddRange([
            New(cmd, "@id"), New(cmd, "@agent"), New(cmd, "@workspace"), New(cmd, "@sid"), New(cmd, "@turn"), New(cmd, "@mid"), New(cmd, "@etype"),
            New(cmd, "@role"), New(cmd, "@content"), New(cmd, "@atts"), New(cmd, "@ts"), New(cmd, "@model"), New(cmd, "@trace"), New(cmd, "@facts"),
            New(cmd, "@schema"), New(cmd, "@record"), New(cmd, "@created")
        ]);
    }

    private static SqliteParameter New(SqliteCommand cmd, string name)
    {
        var p = cmd.CreateParameter(); p.ParameterName = name; return p;
    }

    private static void Bind(SqliteCommand cmd, ObservationRecord r)
    {
        cmd.Parameters["@id"].Value = r.Id;
        cmd.Parameters["@agent"].Value = (object?)r.AgentId ?? DBNull.Value;
        cmd.Parameters["@workspace"].Value = (object?)r.WorkspaceId ?? DBNull.Value;
        cmd.Parameters["@sid"].Value = r.SessionId;
        cmd.Parameters["@turn"].Value = (object?)r.TurnId ?? DBNull.Value;
        cmd.Parameters["@mid"].Value = (object?)r.MessageId ?? DBNull.Value;
        cmd.Parameters["@etype"].Value = (object?)r.EventType ?? DBNull.Value;
        cmd.Parameters["@role"].Value = r.Role;
        cmd.Parameters["@content"].Value = (object?)r.Content ?? DBNull.Value;
        cmd.Parameters["@atts"].Value = r.AttachmentsJson;
        cmd.Parameters["@ts"].Value = r.CreatedTimestamp;
        cmd.Parameters["@model"].Value = (object?)r.ModelName ?? DBNull.Value;
        cmd.Parameters["@trace"].Value = (object?)r.TraceId ?? DBNull.Value;
        cmd.Parameters["@facts"].Value = r.SourceFactsJson;
        cmd.Parameters["@schema"].Value = r.SchemaVersion;
        cmd.Parameters["@record"].Value = r.RecordVersion;
        cmd.Parameters["@created"].Value = r.CreatedAt;
    }

    private static MemorySetting ReadMemorySetting(SqliteDataReader reader)
    {
        return new MemorySetting
        {
            Id = reader.GetString(0),
            AgentId = reader.IsDBNull(1) ? null : reader.GetString(1),
            WorkspaceId = reader.IsDBNull(2) ? null : reader.GetString(2),
            SettingKey = reader.GetString(3),
            SettingValue = reader.GetString(4),
            ValueType = reader.GetString(5),
            Category = reader.GetString(6),
            Description = reader.IsDBNull(7) ? null : reader.GetString(7),
            IsEnabled = reader.GetInt32(8) != 0,
            SchemaVersion = reader.GetInt32(9),
            RecordVersion = reader.GetInt32(10),
            CreatedAt = reader.GetString(11),
            UpdatedAt = reader.GetString(12)
        };
    }

    private static ObservationRecord ReadObservationRecord(SqliteDataReader reader)
    {
        return new ObservationRecord
        {
            Id = reader.GetString(0),
            AgentId = reader.IsDBNull(1) ? null : reader.GetString(1),
            WorkspaceId = reader.IsDBNull(2) ? null : reader.GetString(2),
            SessionId = reader.GetString(3),
            TurnId = reader.IsDBNull(4) ? null : reader.GetString(4),
            MessageId = reader.IsDBNull(5) ? null : reader.GetString(5),
            EventType = reader.IsDBNull(6) ? null : reader.GetString(6),
            Role = reader.GetString(7),
            Content = reader.IsDBNull(8) ? null : reader.GetString(8),
            AttachmentsJson = reader.GetString(9),
            CreatedTimestamp = reader.GetInt64(10),
            ModelName = reader.IsDBNull(11) ? null : reader.GetString(11),
            TraceId = reader.IsDBNull(12) ? null : reader.GetString(12),
            SourceFactsJson = reader.GetString(13),
            SchemaVersion = reader.GetInt32(14),
            RecordVersion = reader.GetInt32(15),
            CreatedAt = reader.GetString(16)
        };
    }

    private static MemoryProcessingState ReadProcessingState(SqliteDataReader reader)
    {
        return new MemoryProcessingState
        {
            Id = reader.GetString(0),
            ProcessorName = reader.GetString(1),
            AgentId = reader.IsDBNull(2) ? null : reader.GetString(2),
            WorkspaceId = reader.IsDBNull(3) ? null : reader.GetString(3),
            State = reader.GetString(4),
            LastObservationTimestamp = reader.GetInt64(5),
            LastObservationId = reader.IsDBNull(6) ? null : reader.GetString(6),
            ProcessedCount = reader.GetInt32(7),
            CreatedFragmentCount = reader.GetInt32(8),
            MergedFragmentCount = reader.GetInt32(9),
            CreatedAbstractionCount = reader.GetInt32(10),
            LastError = reader.IsDBNull(11) ? null : reader.GetString(11),
            LockedUntil = reader.IsDBNull(12) ? null : reader.GetString(12),
            CreatedAt = reader.GetString(13),
            UpdatedAt = reader.GetString(14)
        };
    }

    private static MemoryFragment ReadMemoryFragment(SqliteDataReader reader)
    {
        return new MemoryFragment
        {
            Id = reader.GetString(0),
            AgentId = reader.GetString(1),
            WorkspaceId = reader.IsDBNull(2) ? null : reader.GetString(2),
            MemoryType = reader.GetString(3),
            Topic = reader.GetString(4),
            Title = reader.IsDBNull(5) ? null : reader.GetString(5),
            Summary = reader.GetString(6),
            Detail = reader.IsDBNull(7) ? null : reader.GetString(7),
            KeywordsJson = reader.IsDBNull(8) ? null : reader.GetString(8),
            TagsJson = reader.IsDBNull(9) ? null : reader.GetString(9),
            EntitiesJson = reader.IsDBNull(10) ? null : reader.GetString(10),
            SourceObservationIdsJson = reader.GetString(11),
            SourceSessionIdsJson = reader.IsDBNull(12) ? null : reader.GetString(12),
            SourceTurnIdsJson = reader.IsDBNull(13) ? null : reader.GetString(13),
            Importance = reader.GetDouble(14),
            Confidence = reader.GetDouble(15),
            EmotionalWeight = reader.GetDouble(16),
            Novelty = reader.GetDouble(17),
            SalienceScore = reader.GetDouble(18),
            RetentionScore = reader.GetDouble(19),
            DecayRate = reader.GetDouble(20),
            AccessCount = reader.GetInt32(21),
            ReinforcementCount = reader.GetInt32(22),
            ContradictionCount = reader.GetInt32(23),
            ClarityLevel = reader.GetString(24),
            ConfirmationState = reader.GetString(25),
            LifecycleState = reader.GetString(26),
            LastAccessedAt = reader.IsDBNull(27) ? null : reader.GetString(27),
            LastReinforcedAt = reader.IsDBNull(28) ? null : reader.GetString(28),
            ExpiresAt = reader.IsDBNull(29) ? null : reader.GetString(29),
            SchemaVersion = reader.GetInt32(30),
            RecordVersion = reader.GetInt32(31),
            CompatibilityTagsJson = reader.IsDBNull(32) ? null : reader.GetString(32),
            CreatedAt = reader.GetString(33),
            UpdatedAt = reader.GetString(34)
        };
    }

    private static string CreateProcessingStateId(string processorName, string? agentId, string? workspaceId)
    {
        return $"{processorName}:{agentId ?? string.Empty}:{workspaceId ?? string.Empty}";
    }

    private static MemoryRecallItem ReadRecallItem(SqliteDataReader reader, string? queryText)
    {
        var topic = reader.GetString(4);
        var title = reader.IsDBNull(5) ? null : reader.GetString(5);
        var summary = reader.GetString(6);
        var detail = reader.IsDBNull(7) ? null : reader.GetString(7);
        var baseScore = reader.GetDouble(15);

        return new MemoryRecallItem
        {
            Id = reader.GetString(0),
            Kind = reader.GetString(1),
            AgentId = reader.GetString(2),
            WorkspaceId = reader.IsDBNull(3) ? null : reader.GetString(3),
            Topic = topic,
            Title = title,
            Summary = summary,
            Detail = detail,
            Confidence = reader.GetDouble(8),
            SalienceScore = reader.GetDouble(9),
            RetentionScore = reader.GetDouble(10),
            AccessCount = reader.GetInt32(11),
            LifecycleState = reader.GetString(12),
            ConfirmationState = reader.GetString(13),
            UpdatedAt = reader.GetString(14),
            RecallScore = baseScore + GetTextMatchBoost(queryText, topic, title, summary, detail)
        };
    }

    private static double GetTextMatchBoost(string? queryText, params string?[] values)
    {
        var terms = GetQueryTerms(queryText);
        if (terms.Count == 0) return 0;

        var boost = 0d;
        foreach (var term in terms)
        {
            if (values.Any(value => !string.IsNullOrWhiteSpace(value) && value.Contains(term, StringComparison.OrdinalIgnoreCase))) boost += 0.06;
        }

        return Math.Min(boost, 0.24);
    }

    private static bool HasTextMatch(string? queryText, params string?[] values)
    {
        var terms = GetQueryTerms(queryText);
        return terms.Count == 0 || terms.Any(term => values.Any(value => !string.IsNullOrWhiteSpace(value) && value.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    private static string BuildRecallTextFilter(IReadOnlyList<string> terms, string topicColumn, string titleColumn, string summaryColumn, string detailColumn)
    {
        if (terms.Count == 0) return "1 = 1";

        return string.Join(" OR ", Enumerable.Range(0, terms.Count).Select(i =>
            $"{topicColumn} LIKE @term{i} ESCAPE '\\' OR ifnull({titleColumn}, '') LIKE @term{i} ESCAPE '\\' OR {summaryColumn} LIKE @term{i} ESCAPE '\\' OR ifnull({detailColumn}, '') LIKE @term{i} ESCAPE '\\'"));
    }

    private static string EscapeLikeTerm(string term)
    {
        return term.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> GetQueryTerms(string? queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText)) return [];

        var terms = queryText
            .Split([' ', '\t', '\r', '\n', ',', '.', ';', ':', '，', '。', '；', '：', '！', '？', '!', '?', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static term => term.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        if (terms.Count == 0 && queryText.Trim().Length >= 2) terms.Add(queryText.Trim());
        return terms;
    }

    private void ExecuteMemoryCommand(string commandText, Action<SqliteCommand> bind)
    {
        using var conn = database.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = commandText;
        bind(cmd);
        cmd.ExecuteNonQuery();
    }

    private static void Set(SqliteCommand cmd, string name, object value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }

    private static void SetNullable(SqliteCommand cmd, string name, string? value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
        cmd.Parameters.Add(parameter);
    }

    private static MemoryStorageException CreateStorageException(string operation, SqliteException exception)
    {
        return new MemoryStorageException($"记忆存储操作失败：{operation}", exception);
    }
}
