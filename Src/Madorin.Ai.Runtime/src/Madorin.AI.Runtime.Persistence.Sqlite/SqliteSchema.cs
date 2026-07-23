using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Sqlite;

/// <summary>Creates and migrates the Runtime SQLite metadata schema.</summary>
public static class SqliteSchema
{
    public const int CurrentVersion = 11;

    private static readonly string[] RequiredTables =
    [
        "schema_versions",
        "sessions",
        "runs",
        "run_idempotency",
        "session_idempotency",
        "event_outbox",
        "gsn_sequence",
        "tool_intents",
        "work_steps",
        "work_sessions",
        "work_step_dependencies",
        "work_step_attempts",
        "work_background_jobs",
        "message_index",
        "session_selections",
        "agent_snapshots",
        "tool_grants",
        "tool_approvals",
        "tool_audit",
        "invocation_snapshots",
        "meeting_sessions",
        "meeting_rounds",
        "meeting_participants",
        "meeting_invocations"
    ];

    private static readonly string[] RequiredIndexes =
    [
        "idx_sessions_updated",
        "idx_sessions_status",
        "idx_runs_session",
        "idx_runs_status",
        "idx_outbox_gsn",
        "idx_outbox_status",
        "idx_message_index_session_sequence",
        "idx_tool_intents_status",
        "idx_tool_intents_run",
        "idx_tool_grants_run",
        "idx_tool_grants_parent",
        "idx_tool_approvals_call",
        "idx_tool_audit_call",
        "idx_invocation_snapshots_run",
        "idx_invocation_snapshots_session",
        "idx_meeting_sessions_status",
        "idx_meeting_sessions_pending_approval",
        "idx_meeting_rounds_status",
        "idx_meeting_participants_join_order",
        "idx_meeting_invocations_scheduled",
        "idx_meeting_invocations_run_round",
        "idx_meeting_invocations_session_round_ordinal",
        "idx_work_sessions_status",
        "idx_work_steps_session",
        "idx_work_steps_run",
        "idx_work_steps_status",
        "idx_work_steps_agent",
        "idx_work_step_dependencies_step",
        "idx_work_step_dependencies_depends",
        "idx_work_step_attempts_step",
        "idx_work_background_jobs_session",
        "idx_work_background_jobs_step",
        "idx_tool_intents_work_step"
    ];

    private static readonly (string Table, string Column)[] RequiredColumns =
    [
        ("session_idempotency", "request_hash"),
        ("run_idempotency", "request_hash"),
        ("runs", "terminal_text"),
        ("tool_intents", "agent_id"),
        ("tool_intents", "parent_agent_id"),
        ("tool_intents", "catalog_version"),
        ("tool_intents", "sent_at"),
        ("tool_intents", "grant_id"),
        ("tool_intents", "approval_request_id"),
        ("tool_intents", "result_hash"),
        ("tool_intents", "result_blob_id"),
        ("tool_intents", "result_blob_length"),
        ("tool_intents", "result_blob_sha256"),
        ("tool_intents", "result_blob_content_type"),
        ("tool_intents", "result_blob_access_scope"),
        ("tool_intents", "result_blob_expires_at"),
        ("tool_intents", "error_code"),
        ("tool_intents", "result_visible"),
        ("tool_grants", "approval_request_id"),
        ("tool_audit", "duration_ms"),
        ("meeting_sessions", "selector_state"),
        ("meeting_sessions", "policy_json"),
        ("meeting_sessions", "policy_hash"),
        ("meeting_sessions", "selection_version"),
        ("meeting_sessions", "pending_approval_request_id"),
        ("meeting_sessions", "pending_approval_json"),
        ("meeting_sessions", "pending_approval_status"),
        ("meeting_rounds", "first_invocation_id"),
        ("meeting_rounds", "summary_message_id"),
        ("meeting_rounds", "summarizes_through_seq"),
        ("meeting_rounds", "summary_policy_hash"),
        ("meeting_rounds", "summary_agent_id"),
        ("meeting_rounds", "summary_invocation_id"),
        ("meeting_participants", "agent_id"),
        ("meeting_participants", "agent_ref_json"),
        ("meeting_participants", "display_name"),
        ("meeting_participants", "join_order"),
        ("meeting_participants", "joined_selection_version"),
        ("meeting_participants", "removed_selection_version"),
        ("meeting_participants", "removed_at"),
        ("meeting_invocations", "ordinal"),
        ("meeting_invocations", "participant_id"),
        ("meeting_invocations", "agent_id"),
        ("meeting_invocations", "role"),
        ("meeting_invocations", "selection_version"),
        ("meeting_invocations", "retry_count"),
        ("meeting_invocations", "message_id"),
        ("meeting_invocations", "selector_decision_json"),
        ("meeting_invocations", "scheduled_at"),
        ("meeting_invocations", "error_code"),
        ("meeting_invocations", "error_message"),
        ("work_sessions", "status"),
        ("work_sessions", "general_manager_id"),
        ("work_sessions", "workflow_policy_json"),
        ("work_sessions", "plan_version"),
        ("work_sessions", "plan_message_id"),
        ("work_steps", "parent_step_id"),
        ("work_steps", "invocation_id"),
        ("work_steps", "depth"),
        ("work_steps", "step_index"),
        ("work_steps", "attempt_count"),
        ("work_steps", "error_message"),
        ("work_steps", "started_at"),
        ("work_steps", "title"),
        ("work_steps", "goal"),
        ("work_steps", "is_background"),
        ("tool_intents", "work_step_id"),
        ("tool_intents", "plan_version")
    ];

    /// <summary>Applies connection settings and migrates the database to the current version.</summary>
    public static async Task EnsureCreatedAsync(
        SqliteConnection conn,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conn);
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
        }

        await ConfigureConnectionAsync(conn, ct).ConfigureAwait(false);
        await EnsureVersionTableAsync(conn, ct).ConfigureAwait(false);
        var version = await GetCurrentVersionAsync(conn, ct).ConfigureAwait(false);
        if (version > CurrentVersion)
        {
            throw new InvalidDataException(
                $"The database schema version {version} is newer than supported version {CurrentVersion}.");
        }

        if (version < CurrentVersion)
        {
            await MigrateAsync(conn, version, ct).ConfigureAwait(false);
        }

        await EnsureVersionEightBlobColumnsAsync(conn, ct).ConfigureAwait(false);
        await EnsureVersionEightGrantColumnsAsync(conn, ct).ConfigureAwait(false);
        await EnsureVersionEightAuditColumnsAsync(conn, ct).ConfigureAwait(false);

        await ValidateAsync(conn, ct).ConfigureAwait(false);
    }

    /// <summary>Applies forward-only migrations beginning at the supplied version.</summary>
    public static async Task MigrateAsync(
        SqliteConnection conn,
        int fromVersion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conn);
        if (fromVersion < 0 || fromVersion > CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(fromVersion));
        }

        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
        }

        await ConfigureConnectionAsync(conn, ct).ConfigureAwait(false);
        await EnsureVersionTableAsync(conn, ct).ConfigureAwait(false);
        var actualVersion = await GetCurrentVersionAsync(conn, ct).ConfigureAwait(false);
        if (actualVersion != fromVersion)
        {
            throw new InvalidOperationException(
                $"Migration expected schema version {fromVersion}, but the database is version {actualVersion}.");
        }

        for (var version = fromVersion + 1; version <= CurrentVersion; version++)
        {
            switch (version)
            {
                case 1:
                    await ApplyVersionOneAsync(conn, ct).ConfigureAwait(false);
                    break;
                case 2:
                    await ApplyVersionTwoAsync(conn, ct).ConfigureAwait(false);
                    break;
                case 3:
                    await ApplyVersionThreeAsync(conn, ct).ConfigureAwait(false);
                    break;
                case 4:
                    await ApplyVersionFourAsync(conn, ct).ConfigureAwait(false);
                    break;
                case 5:
                    await ApplyVersionFiveAsync(conn, ct).ConfigureAwait(false);
                    break;
                case 6:
                    await ApplyVersionSixAsync(conn, ct).ConfigureAwait(false);
                    break;
                case 7:
                    await ApplyVersionSevenAsync(conn, ct).ConfigureAwait(false);
                    break;
                case 8:
                    await ApplyVersionEightAsync(conn, ct).ConfigureAwait(false);
                    break;
                case 9:
                    await ApplyVersionNineAsync(conn, ct).ConfigureAwait(false);
                    break;
                case 10:
                    await ApplyVersionTenAsync(conn, ct).ConfigureAwait(false);
                    break;
                case 11:
                    await ApplyVersionElevenAsync(conn, ct).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException($"No migration is registered for schema version {version}.");
            }
        }

        if (fromVersion < CurrentVersion)
        {
            await ValidateAsync(conn, ct).ConfigureAwait(false);
        }
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var journalCommand = conn.CreateCommand();
        journalCommand.CommandText = "PRAGMA journal_mode = WAL;";
        await journalCommand.ExecuteScalarAsync(ct).ConfigureAwait(false);

        await using var settingsCommand = conn.CreateCommand();
        settingsCommand.CommandText = """
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            PRAGMA synchronous = NORMAL;
            """;
        await settingsCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task EnsureVersionTableAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var command = conn.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_versions(
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> GetCurrentVersionAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var command = conn.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_versions;";
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ApplyVersionOneAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var transaction = conn.BeginTransaction(deferred: false);
        await using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions(
                session_id TEXT PRIMARY KEY,
                mode TEXT NOT NULL,
                status TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS runs(
                run_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL REFERENCES sessions(session_id),
                status TEXT NOT NULL,
                run_sequence INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS run_idempotency(
                key TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                expires_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS session_idempotency(
                key TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                expires_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS event_outbox(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                gsn INTEGER NOT NULL UNIQUE,
                run_id TEXT NOT NULL,
                run_sequence INTEGER NOT NULL,
                message_type TEXT NOT NULL,
                payload TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'pending',
                created_at TEXT NOT NULL,
                acked_at TEXT
            );
            CREATE TABLE IF NOT EXISTS gsn_sequence(
                id INTEGER PRIMARY KEY CHECK(id = 1),
                next_gsn INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS idx_sessions_updated ON sessions(updated_at, session_id);
            CREATE INDEX IF NOT EXISTS idx_sessions_status ON sessions(status);
            CREATE INDEX IF NOT EXISTS idx_runs_session ON runs(session_id);
            CREATE INDEX IF NOT EXISTS idx_runs_status ON runs(status);
            CREATE INDEX IF NOT EXISTS idx_outbox_gsn ON event_outbox(gsn);
            CREATE INDEX IF NOT EXISTS idx_outbox_status ON event_outbox(status);
            INSERT OR IGNORE INTO gsn_sequence(id, next_gsn) VALUES(1, 1);
            INSERT OR IGNORE INTO schema_versions(version, applied_at)
            VALUES(1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        transaction.Commit();
    }

    private static async Task ApplyVersionTwoAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var transaction = conn.BeginTransaction(deferred: false);
        await using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS tool_intents(
                call_id TEXT PRIMARY KEY,
                invocation_id TEXT,
                run_id TEXT,
                session_id TEXT,
                tool_id TEXT,
                arguments_hash TEXT,
                status TEXT DEFAULT 'pending',
                result_json TEXT,
                error_message TEXT,
                created_at TEXT,
                completed_at TEXT
            );
            INSERT OR IGNORE INTO schema_versions(version, applied_at)
            VALUES(2, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        transaction.Commit();
    }

    private static async Task ApplyVersionThreeAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var transaction = conn.BeginTransaction(deferred: false);
        await using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS message_index(
                message_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                invocation_id TEXT NOT NULL,
                agent_id TEXT NOT NULL,
                role TEXT NOT NULL,
                file_offset INTEGER NOT NULL,
                record_length INTEGER NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_message_index_session_sequence
            ON message_index(session_id, sequence);
            INSERT OR IGNORE INTO schema_versions(version, applied_at)
            VALUES(3, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        transaction.Commit();
    }

    private static async Task ApplyVersionFourAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var transaction = conn.BeginTransaction(deferred: false);
        await using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS work_steps (
                step_id TEXT NOT NULL,
                plan_version TEXT NOT NULL,
                run_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                target_agent_id TEXT,
                status TEXT DEFAULT 'pending',
                step_input_hash TEXT,
                result_json TEXT,
                created_at TEXT,
                completed_at TEXT,
                PRIMARY KEY (step_id, plan_version)
            );
            INSERT OR IGNORE INTO schema_versions(version, applied_at)
            VALUES(4, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        transaction.Commit();
    }

    private static async Task ApplyVersionFiveAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var transaction = conn.BeginTransaction(deferred: false);
        await using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE session_idempotency ADD COLUMN request_hash TEXT;
            ALTER TABLE run_idempotency ADD COLUMN request_hash TEXT;
            INSERT OR IGNORE INTO schema_versions(version, applied_at)
            VALUES(5, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        transaction.Commit();
    }

    private static async Task ApplyVersionSixAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var transaction = conn.BeginTransaction(deferred: false);
        await using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE runs ADD COLUMN terminal_text TEXT;
            INSERT OR IGNORE INTO schema_versions(version, applied_at)
            VALUES(6, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        transaction.Commit();
    }

    private static async Task ApplyVersionSevenAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var transaction = conn.BeginTransaction(deferred: false);
        await using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS session_selections(
                session_id TEXT PRIMARY KEY REFERENCES sessions(session_id) ON DELETE CASCADE,
                selection_version INTEGER NOT NULL,
                selection_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS agent_snapshots(
                session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
                agent_id TEXT NOT NULL,
                prompt_template_version TEXT NOT NULL,
                prompt_hash TEXT NOT NULL,
                provider_id TEXT,
                model_id TEXT,
                PRIMARY KEY(session_id, agent_id)
            );
            INSERT OR IGNORE INTO schema_versions(version, applied_at)
            VALUES(7, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        transaction.Commit();
    }

    private static async Task ApplyVersionEightAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var transaction = conn.BeginTransaction(deferred: false);
        await using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE tool_intents ADD COLUMN agent_id TEXT;
            ALTER TABLE tool_intents ADD COLUMN parent_agent_id TEXT;
            ALTER TABLE tool_intents ADD COLUMN catalog_version TEXT;
            ALTER TABLE tool_intents ADD COLUMN sent_at TEXT;
            ALTER TABLE tool_intents ADD COLUMN grant_id TEXT;
            ALTER TABLE tool_intents ADD COLUMN approval_request_id TEXT;
            ALTER TABLE tool_intents ADD COLUMN result_hash TEXT;
            ALTER TABLE tool_intents ADD COLUMN result_blob_id TEXT;
            ALTER TABLE tool_intents ADD COLUMN result_blob_length INTEGER;
            ALTER TABLE tool_intents ADD COLUMN result_blob_sha256 TEXT;
            ALTER TABLE tool_intents ADD COLUMN result_blob_content_type TEXT;
            ALTER TABLE tool_intents ADD COLUMN result_blob_access_scope TEXT;
            ALTER TABLE tool_intents ADD COLUMN result_blob_expires_at TEXT;
            ALTER TABLE tool_intents ADD COLUMN error_code TEXT;
            ALTER TABLE tool_intents ADD COLUMN result_visible INTEGER NOT NULL DEFAULT 1;

            CREATE TABLE tool_grants(
                grant_id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                agent_id TEXT,
                parent_grant_id TEXT REFERENCES tool_grants(grant_id),
                root_grant_id TEXT NOT NULL,
                workspace_root TEXT NOT NULL,
                allowed_tool_ids TEXT NOT NULL,
                allowed_call_ids TEXT NOT NULL,
                maximum_risk INTEGER NOT NULL,
                allowed_read_roots TEXT NOT NULL,
                allowed_write_roots TEXT NOT NULL,
                allow_overwrite INTEGER NOT NULL,
                allow_move INTEGER NOT NULL,
                allow_delete INTEGER NOT NULL,
                allowed_executables TEXT NOT NULL,
                allowed_environment_variables TEXT NOT NULL,
                allow_powershell INTEGER NOT NULL,
                network_policy_json TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                allow_delegation INTEGER NOT NULL,
                delegated_agent_ids TEXT NOT NULL,
                delegation_chain TEXT NOT NULL,
                created_at TEXT NOT NULL,
                revoked_at TEXT,
                revocation_reason TEXT,
                approval_request_id TEXT
            );
            CREATE TABLE tool_approvals(
                approval_request_id TEXT PRIMARY KEY,
                call_id TEXT NOT NULL,
                run_id TEXT NOT NULL,
                agent_id TEXT NOT NULL,
                tool_id TEXT NOT NULL,
                arguments_hash TEXT NOT NULL,
                decision TEXT NOT NULL,
                grant_id TEXT,
                reason TEXT,
                requested_at TEXT NOT NULL,
                decided_at TEXT
            );
            CREATE TABLE tool_audit(
                audit_id TEXT PRIMARY KEY,
                call_id TEXT NOT NULL,
                run_id TEXT NOT NULL,
                agent_id TEXT NOT NULL,
                parent_agent_id TEXT,
                tool_id TEXT NOT NULL,
                catalog_version TEXT NOT NULL,
                risk INTEGER NOT NULL,
                arguments_hash TEXT NOT NULL,
                target_summary TEXT NOT NULL,
                grant_id TEXT,
                approval_request_id TEXT,
                delegation_chain TEXT NOT NULL,
                status TEXT NOT NULL,
                result_hash TEXT,
                diagnostic_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                completed_at TEXT,
                duration_ms INTEGER
            );
            CREATE INDEX idx_tool_intents_status ON tool_intents(status, sent_at);
            CREATE INDEX idx_tool_intents_run ON tool_intents(run_id, created_at);
            CREATE INDEX idx_tool_grants_run ON tool_grants(run_id, revoked_at, expires_at);
            CREATE INDEX idx_tool_grants_parent ON tool_grants(parent_grant_id);
            CREATE INDEX idx_tool_approvals_call ON tool_approvals(call_id);
            CREATE INDEX idx_tool_audit_call ON tool_audit(call_id, created_at);
            INSERT INTO schema_versions(version, applied_at)
            VALUES(8, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        transaction.Commit();
    }


    private static async Task ApplyVersionNineAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var transaction = conn.BeginTransaction(deferred: false);
        await using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS invocation_snapshots(
                invocation_id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                snapshot_json TEXT NOT NULL,
                started_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_invocation_snapshots_run
            ON invocation_snapshots(run_id);
            CREATE INDEX IF NOT EXISTS idx_invocation_snapshots_session
            ON invocation_snapshots(session_id, started_at);
            INSERT OR IGNORE INTO schema_versions(version, applied_at)
            VALUES(9, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        transaction.Commit();
    }

    private static async Task ApplyVersionTenAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var transaction = conn.BeginTransaction(deferred: false);
        await using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS meeting_sessions(
                session_id TEXT PRIMARY KEY REFERENCES sessions(session_id) ON DELETE CASCADE,
                run_id TEXT NOT NULL,
                status TEXT NOT NULL,
                current_round INTEGER NOT NULL DEFAULT 0,
                selector_state BLOB,
                policy_json TEXT NOT NULL DEFAULT '{}',
                policy_hash TEXT NOT NULL DEFAULT '',
                selection_version INTEGER NOT NULL DEFAULT 0,
                pending_approval_request_id TEXT,
                pending_approval_json TEXT,
                pending_approval_status TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS meeting_rounds(
                session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
                round_index INTEGER NOT NULL,
                run_id TEXT NOT NULL,
                status TEXT NOT NULL,
                first_invocation_id TEXT,
                summary_message_id TEXT,
                summarizes_through_seq INTEGER,
                summary_policy_hash TEXT,
                summary_agent_id TEXT,
                summary_invocation_id TEXT,
                created_at TEXT NOT NULL,
                started_at TEXT,
                completed_at TEXT,
                PRIMARY KEY (session_id, round_index)
            );
            CREATE TABLE IF NOT EXISTS meeting_participants(
                session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
                participant_id TEXT NOT NULL,
                agent_id TEXT NOT NULL,
                agent_ref_json TEXT,
                display_name TEXT,
                join_order INTEGER NOT NULL,
                status TEXT NOT NULL DEFAULT 'active',
                joined_selection_version INTEGER NOT NULL,
                removed_selection_version INTEGER,
                joined_at TEXT NOT NULL,
                removed_at TEXT,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (session_id, participant_id)
            );
            CREATE TABLE IF NOT EXISTS meeting_invocations(
                invocation_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
                run_id TEXT NOT NULL,
                round_index INTEGER NOT NULL,
                ordinal INTEGER NOT NULL,
                participant_id TEXT,
                agent_id TEXT,
                role TEXT,
                status TEXT NOT NULL,
                selection_version INTEGER NOT NULL,
                retry_count INTEGER NOT NULL DEFAULT 0,
                message_id TEXT,
                selector_decision_json TEXT,
                scheduled_at TEXT NOT NULL,
                started_at TEXT,
                completed_at TEXT,
                error_code TEXT,
                error_message TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_meeting_invocations_session_round_ordinal
            ON meeting_invocations(session_id, round_index, ordinal);
            CREATE INDEX IF NOT EXISTS idx_meeting_sessions_status
            ON meeting_sessions(status, updated_at);
            CREATE INDEX IF NOT EXISTS idx_meeting_sessions_pending_approval
            ON meeting_sessions(pending_approval_request_id)
            WHERE pending_approval_request_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS idx_meeting_rounds_status
            ON meeting_rounds(session_id, status, round_index);
            CREATE INDEX IF NOT EXISTS idx_meeting_participants_join_order
            ON meeting_participants(session_id, join_order, participant_id);
            CREATE INDEX IF NOT EXISTS idx_meeting_invocations_scheduled
            ON meeting_invocations(session_id, status, scheduled_at);
            CREATE INDEX IF NOT EXISTS idx_meeting_invocations_run_round
            ON meeting_invocations(run_id, round_index);
            INSERT OR IGNORE INTO schema_versions(version, applied_at)
            VALUES(10, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        transaction.Commit();
    }


    private static async Task ApplyVersionElevenAsync(SqliteConnection conn, CancellationToken ct)
    {
        using var transaction = conn.BeginTransaction(deferred: false);
        await using (var command = conn.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS work_sessions (
                    session_id TEXT PRIMARY KEY REFERENCES sessions(session_id) ON DELETE CASCADE,
                    run_id TEXT NOT NULL,
                    status TEXT NOT NULL,
                    general_manager_id TEXT NOT NULL,
                    workflow_policy_json TEXT NOT NULL DEFAULT '{}',
                    context_policy_json TEXT NOT NULL DEFAULT '{}',
                    plan_version TEXT NOT NULL DEFAULT '1',
                    plan_message_id TEXT,
                    plan_json TEXT,
                    pending_approval_request_id TEXT,
                    pending_approval_json TEXT,
                    requires_manual_intervention INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_work_sessions_status
                ON work_sessions(status, updated_at);

                CREATE TABLE IF NOT EXISTS work_step_dependencies (
                    step_id TEXT NOT NULL,
                    plan_version TEXT NOT NULL,
                    depends_on_step_id TEXT NOT NULL,
                    PRIMARY KEY (step_id, plan_version, depends_on_step_id)
                );
                CREATE INDEX IF NOT EXISTS idx_work_step_dependencies_step
                ON work_step_dependencies(step_id, plan_version);
                CREATE INDEX IF NOT EXISTS idx_work_step_dependencies_depends
                ON work_step_dependencies(depends_on_step_id, plan_version);

                CREATE TABLE IF NOT EXISTS work_step_attempts (
                    step_id TEXT NOT NULL,
                    plan_version TEXT NOT NULL,
                    attempt_number INTEGER NOT NULL,
                    invocation_id TEXT NOT NULL,
                    status TEXT NOT NULL,
                    error_message TEXT,
                    started_at TEXT,
                    completed_at TEXT,
                    PRIMARY KEY (step_id, plan_version, attempt_number)
                );
                CREATE INDEX IF NOT EXISTS idx_work_step_attempts_step
                ON work_step_attempts(step_id, plan_version, attempt_number);

                CREATE TABLE IF NOT EXISTS work_background_jobs (
                    job_id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    run_id TEXT NOT NULL,
                    step_id TEXT NOT NULL,
                    plan_version TEXT NOT NULL,
                    status TEXT NOT NULL,
                    error_message TEXT,
                    created_at TEXT NOT NULL,
                    started_at TEXT,
                    completed_at TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_work_background_jobs_session
                ON work_background_jobs(session_id, status);
                CREATE INDEX IF NOT EXISTS idx_work_background_jobs_step
                ON work_background_jobs(step_id, plan_version);
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await AddColumnIfMissingAsync(conn, transaction, "work_steps", "parent_step_id", "TEXT", ct)
            .ConfigureAwait(false);
        await AddColumnIfMissingAsync(conn, transaction, "work_steps", "invocation_id", "TEXT", ct)
            .ConfigureAwait(false);
        await AddColumnIfMissingAsync(conn, transaction, "work_steps", "depth", "INTEGER NOT NULL DEFAULT 0", ct)
            .ConfigureAwait(false);
        await AddColumnIfMissingAsync(conn, transaction, "work_steps", "step_index", "INTEGER NOT NULL DEFAULT 0", ct)
            .ConfigureAwait(false);
        await AddColumnIfMissingAsync(conn, transaction, "work_steps", "attempt_count", "INTEGER NOT NULL DEFAULT 0", ct)
            .ConfigureAwait(false);
        await AddColumnIfMissingAsync(conn, transaction, "work_steps", "error_message", "TEXT", ct)
            .ConfigureAwait(false);
        await AddColumnIfMissingAsync(conn, transaction, "work_steps", "started_at", "TEXT", ct)
            .ConfigureAwait(false);
        await AddColumnIfMissingAsync(conn, transaction, "work_steps", "title", "TEXT", ct)
            .ConfigureAwait(false);
        await AddColumnIfMissingAsync(conn, transaction, "work_steps", "goal", "TEXT", ct)
            .ConfigureAwait(false);
        await AddColumnIfMissingAsync(conn, transaction, "work_steps", "is_background", "INTEGER NOT NULL DEFAULT 0", ct)
            .ConfigureAwait(false);
        await AddColumnIfMissingAsync(conn, transaction, "work_steps", "depends_json", "TEXT", ct)
            .ConfigureAwait(false);
        await AddColumnIfMissingAsync(conn, transaction, "tool_intents", "work_step_id", "TEXT", ct)
            .ConfigureAwait(false);
        await AddColumnIfMissingAsync(conn, transaction, "tool_intents", "plan_version", "TEXT", ct)
            .ConfigureAwait(false);

        await using (var indexCommand = conn.CreateCommand())
        {
            indexCommand.Transaction = transaction;
            indexCommand.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_work_steps_session
                ON work_steps(session_id, plan_version, status);
                CREATE INDEX IF NOT EXISTS idx_work_steps_run
                ON work_steps(run_id, status);
                CREATE INDEX IF NOT EXISTS idx_work_steps_status
                ON work_steps(status, session_id);
                CREATE INDEX IF NOT EXISTS idx_work_steps_agent
                ON work_steps(target_agent_id, status);
                CREATE INDEX IF NOT EXISTS idx_tool_intents_work_step
                ON tool_intents(work_step_id, plan_version);
                INSERT OR IGNORE INTO schema_versions(version, applied_at)
                VALUES(11, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                """;
            await indexCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    private static async Task AddColumnIfMissingAsync(
        SqliteConnection conn,
        SqliteTransaction transaction,
        string table,
        string column,
        string definition,
        CancellationToken ct)
    {
        if (await ColumnExistsAsync(conn, table, column, ct).ConfigureAwait(false))
        {
            return;
        }

        await using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task EnsureVersionEightBlobColumnsAsync(
        SqliteConnection conn,
        CancellationToken ct)
    {
        if (!await SchemaObjectExistsAsync(conn, "table", "tool_intents", ct)
                .ConfigureAwait(false))
        {
            return;
        }

        var columns = new (string Name, string Sql)[]
        {
            ("result_blob_length", "ALTER TABLE tool_intents ADD COLUMN result_blob_length INTEGER;"),
            ("result_blob_sha256", "ALTER TABLE tool_intents ADD COLUMN result_blob_sha256 TEXT;"),
            ("result_blob_content_type", "ALTER TABLE tool_intents ADD COLUMN result_blob_content_type TEXT;"),
            ("result_blob_access_scope", "ALTER TABLE tool_intents ADD COLUMN result_blob_access_scope TEXT;"),
            ("result_blob_expires_at", "ALTER TABLE tool_intents ADD COLUMN result_blob_expires_at TEXT;")
        };
        var missing = new List<string>();
        foreach (var column in columns)
        {
            if (!await ColumnExistsAsync(conn, "tool_intents", column.Name, ct)
                    .ConfigureAwait(false))
            {
                missing.Add(column.Sql);
            }
        }

        if (missing.Count == 0)
        {
            return;
        }

        using var transaction = conn.BeginTransaction(deferred: false);
        foreach (var sql in missing)
        {
            await using var command = conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    private static async Task EnsureVersionEightAuditColumnsAsync(
        SqliteConnection conn,
        CancellationToken ct)
    {
        if (!await SchemaObjectExistsAsync(conn, "table", "tool_audit", ct)
                .ConfigureAwait(false)
            || await ColumnExistsAsync(conn, "tool_audit", "duration_ms", ct)
                .ConfigureAwait(false))
        {
            return;
        }

        using var transaction = conn.BeginTransaction(deferred: false);
        await using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "ALTER TABLE tool_audit ADD COLUMN duration_ms INTEGER;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        transaction.Commit();
    }

    private static async Task EnsureVersionEightGrantColumnsAsync(
        SqliteConnection conn,
        CancellationToken ct)
    {
        if (!await SchemaObjectExistsAsync(conn, "table", "tool_grants", ct)
                .ConfigureAwait(false)
            || await ColumnExistsAsync(conn, "tool_grants", "approval_request_id", ct)
                .ConfigureAwait(false))
        {
            return;
        }

        using var transaction = conn.BeginTransaction(deferred: false);
        await using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "ALTER TABLE tool_grants ADD COLUMN approval_request_id TEXT;";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        transaction.Commit();
    }

    private static async Task ValidateAsync(SqliteConnection conn, CancellationToken ct)
    {
        var missingObjects = new List<string>();
        foreach (var table in RequiredTables)
        {
            if (!await SchemaObjectExistsAsync(conn, "table", table, ct).ConfigureAwait(false))
            {
                missingObjects.Add(table);
            }
        }

        foreach (var index in RequiredIndexes)
        {
            if (!await SchemaObjectExistsAsync(conn, "index", index, ct).ConfigureAwait(false))
            {
                missingObjects.Add(index);
            }
        }

        foreach (var (table, column) in RequiredColumns)
        {
            if (!await ColumnExistsAsync(conn, table, column, ct).ConfigureAwait(false))
            {
                missingObjects.Add($"{table}.{column}");
            }
        }

        if (missingObjects.Count > 0)
        {
            throw new InvalidDataException(
                $"SQLite schema version {CurrentVersion} is incomplete. Missing: {string.Join(", ", missingObjects)}.");
        }
    }

    private static async Task<bool> SchemaObjectExistsAsync(
        SqliteConnection conn,
        string type,
        string name,
        CancellationToken ct)
    {
        await using var command = conn.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = $type AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection conn,
        string table,
        string column,
        CancellationToken ct)
    {
        await using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM pragma_table_info($table)
            WHERE name = $column
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }
}
