using System.Globalization;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Tests;

[TestClass]
public sealed class SqliteToolStateStoreTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task EnsureCreatedAsync_FromVersionSeven_PreservesToolIntent()
    {
        await using var connection = await CreateDatabaseAsync();
        await DowngradeToVersionSevenAsync(connection);
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO tool_intents(
                    call_id,
                    invocation_id,
                    run_id,
                    session_id,
                    tool_id,
                    arguments_hash,
                    status,
                    created_at)
                VALUES(
                    'call-v7',
                    'invocation-v7',
                    'run-v7',
                    'session-v7',
                    'host.legacy',
                    'ABC123',
                    'pending',
                    '2026-07-22T00:00:00.0000000+00:00');
                """;
            await insert.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);

        using var store = new SqliteToolIntentRepository(connection);
        var intent = await store.GetIntentAsync("call-v7", TestContext.CancellationToken);
        Assert.IsNotNull(intent);
        Assert.AreEqual(ToolIntentStatus.Pending, intent.Status);
        Assert.AreEqual("host.legacy", intent.ToolId);
        Assert.AreEqual(string.Empty, intent.AgentId);
        Assert.IsTrue(intent.IsResultVisible);
        await using var version = connection.CreateCommand();
        version.CommandText = "SELECT MAX(version) FROM schema_versions;";
        Assert.AreEqual(
            SqliteSchema.CurrentVersion,
            Convert.ToInt32(
                await version.ExecuteScalarAsync(TestContext.CancellationToken),
                CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task IntentLifecycle_SentThenCompleted_PersistsRecoveryMetadata()
    {
        await using var connection = await CreateDatabaseAsync();
        using var store = new SqliteToolIntentRepository(connection);
        var createdAt = DateTimeOffset.UtcNow;
        var intent = new ToolIntentState(
            "call-1",
            "invocation-1",
            "run-1",
            "session-1",
            "agent-1",
            ParentAgentId: null,
            "host.orders.create",
            "catalog-v4",
            "A1B2C3",
            ToolIntentStatus.Pending,
            GrantId: null,
            ApprovalRequestId: "approval-1",
            ResultJson: null,
            ResultHash: null,
            ResultBlob: null,
            ErrorCode: null,
            ErrorMessage: null,
            IsResultVisible: true,
            createdAt,
            SentAt: null,
            CompletedAt: null,
            WorkStepId: "step-1",
            PlanVersion: "2");

        Assert.IsTrue(await store.TryCreateIntentAsync(intent, TestContext.CancellationToken));
        Assert.IsFalse(await store.TryCreateIntentAsync(intent, TestContext.CancellationToken));
        Assert.IsTrue(await store.TryMarkSentAsync(
            intent.CallId,
            "grant-1",
            "approval-1",
            createdAt.AddSeconds(1),
            TestContext.CancellationToken));
        Assert.HasCount(
            1,
            await store.ListRecoverableIntentsAsync(TestContext.CancellationToken));
        var stepIntents = await store.ListRecoverableWorkStepIntentsAsync(
            intent.SessionId,
            intent.WorkStepId!,
            intent.PlanVersion!,
            TestContext.CancellationToken);
        var stepIntent = Assert.ContainsSingle(stepIntents);
        Assert.AreEqual(intent.CallId, stepIntent.CallId);
        Assert.AreEqual(intent.WorkStepId, stepIntent.WorkStepId);
        Assert.AreEqual(intent.PlanVersion, stepIntent.PlanVersion);
        Assert.IsTrue(await store.TryCompleteIntentAsync(
            new ToolIntentCompletion(
                intent.CallId,
                ToolIntentStatus.Sent,
                ToolIntentStatus.Succeeded,
                "{\"orderId\":42}",
                "sha256:RESULT",
                new ToolResultBlobState(
                    "blob-result-1",
                    128,
                    "sha256:BLOB",
                    "application/json",
                    "run",
                    createdAt.AddDays(1)),
                ErrorCode: null,
                ErrorMessage: null,
                IsResultVisible: false,
                createdAt.AddSeconds(2)),
            TestContext.CancellationToken));

        var completed = await store.GetIntentAsync(intent.CallId, TestContext.CancellationToken);
        Assert.IsNotNull(completed);
        Assert.AreEqual(ToolIntentStatus.Succeeded, completed.Status);
        Assert.AreEqual("grant-1", completed.GrantId);
        Assert.AreEqual("approval-1", completed.ApprovalRequestId);
        Assert.AreEqual("sha256:RESULT", completed.ResultHash);
        Assert.IsNotNull(completed.ResultBlob);
        Assert.AreEqual("blob-result-1", completed.ResultBlob.BlobId);
        Assert.AreEqual(128, completed.ResultBlob.Length);
        Assert.AreEqual("sha256:BLOB", completed.ResultBlob.Sha256);
        Assert.AreEqual("application/json", completed.ResultBlob.ContentType);
        Assert.AreEqual("run", completed.ResultBlob.AccessScope);
        Assert.IsFalse(completed.IsResultVisible);
        Assert.AreEqual(intent.WorkStepId, completed.WorkStepId);
        Assert.AreEqual(intent.PlanVersion, completed.PlanVersion);
        Assert.IsEmpty(await store.ListRecoverableIntentsAsync(TestContext.CancellationToken));
        Assert.IsEmpty(await store.ListRecoverableWorkStepIntentsAsync(
            intent.SessionId,
            intent.WorkStepId!,
            intent.PlanVersion!,
            TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task GrantApprovalAndAudit_RoundTripAndRecursiveRevoke()
    {
        await using var connection = await CreateDatabaseAsync();
        using var store = new SqliteToolIntentRepository(connection);
        var now = DateTimeOffset.UtcNow;
        var root = CreateGrant("grant-root", parentGrantId: null, "agent-root", now);
        var child = CreateGrant("grant-child", root.GrantId, "agent-child", now) with
        {
            RootGrantId = root.GrantId,
            DelegationChain = [root.GrantId],
            ApprovalRequestId = "approval-1"
        };

        await store.UpsertGrantAsync(root, TestContext.CancellationToken);
        await store.UpsertGrantAsync(root, TestContext.CancellationToken);
        await store.UpsertGrantAsync(child, TestContext.CancellationToken);
        Assert.HasCount(
            2,
            await store.ListActiveGrantsAsync(
                root.RunId,
                now,
                TestContext.CancellationToken));

        var approval = new ToolApprovalState(
            "approval-1",
            "call-1",
            root.RunId,
            "agent-child",
            "host.orders.create",
            "HASH-1",
            "needsApproval",
            GrantId: null,
            Reason: null,
            now,
            DecidedAt: null);
        await store.SaveApprovalAsync(approval, TestContext.CancellationToken);
        await store.SaveApprovalAsync(
            approval with
            {
                Decision = "granted",
                GrantId = child.GrantId,
                DecidedAt = now.AddSeconds(1)
            },
            TestContext.CancellationToken);
        var storedApproval = await store.GetApprovalAsync(
            approval.ApprovalRequestId,
            TestContext.CancellationToken);
        Assert.IsNotNull(storedApproval);
        Assert.AreEqual("granted", storedApproval.Decision);
        Assert.AreEqual(child.GrantId, storedApproval.GrantId);
        Assert.AreEqual(
            approval.ApprovalRequestId,
            (await store.GetGrantAsync(child.GrantId, TestContext.CancellationToken))
                ?.ApprovalRequestId);

        var audit = new ToolAuditRecord(
            "audit-1",
            approval.CallId,
            root.RunId,
            "agent-child",
            "agent-root",
            approval.ToolId,
            "catalog-v1",
            Risk: 3,
            approval.ArgumentsHash,
            "host.orders.create:order",
            child.GrantId,
            approval.ApprovalRequestId,
            [root.GrantId, child.GrantId],
            "prepared",
            ResultHash: null,
            "diagnostic-1",
            now);
        await store.AppendAuditAsync(audit, TestContext.CancellationToken);
        await store.CompleteAuditAsync(
            audit.AuditId,
            "succeeded",
            "sha256:RESULT",
            now.AddSeconds(2),
            TestContext.CancellationToken);

        Assert.AreEqual(
            2,
            await store.RevokeGrantAsync(
                root.GrantId,
                revokeDescendants: true,
                now.AddSeconds(3),
                "user revoked",
                TestContext.CancellationToken));
        Assert.IsEmpty(await store.ListActiveGrantsAsync(
            root.RunId,
            now.AddSeconds(4),
            TestContext.CancellationToken));
        Assert.IsNotNull((await store.GetGrantAsync(
            child.GrantId,
            TestContext.CancellationToken))?.RevokedAt);

        await using var auditQuery = connection.CreateCommand();
        auditQuery.CommandText = """
            SELECT status, result_hash, arguments_hash, target_summary, duration_ms
            FROM tool_audit
            WHERE audit_id = 'audit-1';
            """;
        await using var reader = await auditQuery.ExecuteReaderAsync(TestContext.CancellationToken);
        Assert.IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        Assert.AreEqual("succeeded", reader.GetString(0));
        Assert.AreEqual("sha256:RESULT", reader.GetString(1));
        Assert.AreEqual("HASH-1", reader.GetString(2));
        Assert.AreEqual("host.orders.create:order", reader.GetString(3));
        Assert.AreEqual(2000L, reader.GetInt64(4));
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_VersionEightMissingStageFiveColumns_AddsCompatibleColumns()
    {
        await using var connection = await CreateDatabaseAsync();
        await using (var drop = connection.CreateCommand())
        {
            drop.CommandText = """
                ALTER TABLE tool_audit DROP COLUMN duration_ms;
                ALTER TABLE tool_grants DROP COLUMN approval_request_id;
                """;
            await drop.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);

        await using var column = connection.CreateCommand();
        column.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info('tool_audit')
            WHERE name = 'duration_ms';
            """;
        Assert.AreEqual(
            1L,
            Convert.ToInt64(
                await column.ExecuteScalarAsync(TestContext.CancellationToken),
                CultureInfo.InvariantCulture));
        column.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info('tool_grants')
            WHERE name = 'approval_request_id';
            """;
        Assert.AreEqual(
            1L,
            Convert.ToInt64(
                await column.ExecuteScalarAsync(TestContext.CancellationToken),
                CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public async Task EnsureCreatedAsync_WhenVersionEightMigrationFails_RollsBackColumns()
    {
        await using var connection = await CreateDatabaseAsync();
        await DowngradeToVersionSevenAsync(connection);
        await using (var conflict = connection.CreateCommand())
        {
            conflict.CommandText = "CREATE TABLE tool_grants(id TEXT PRIMARY KEY);";
            await conflict.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        await Assert.ThrowsExactlyAsync<SqliteException>(
            async () => await SqliteSchema.EnsureCreatedAsync(
                connection,
                TestContext.CancellationToken));

        await using var version = connection.CreateCommand();
        version.CommandText = "SELECT MAX(version) FROM schema_versions;";
        Assert.AreEqual(
            7,
            Convert.ToInt32(
                await version.ExecuteScalarAsync(TestContext.CancellationToken),
                CultureInfo.InvariantCulture));
        await using var column = connection.CreateCommand();
        column.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info('tool_intents')
            WHERE name = 'agent_id';
            """;
        Assert.AreEqual(
            0L,
            Convert.ToInt64(
                await column.ExecuteScalarAsync(TestContext.CancellationToken),
                CultureInfo.InvariantCulture));
    }

    private static ToolGrantState CreateGrant(
        string grantId,
        string? parentGrantId,
        string agentId,
        DateTimeOffset now) =>
        new(
            grantId,
            "run-1",
            agentId,
            parentGrantId,
            parentGrantId ?? grantId,
            "C:\\workspace",
            ["host.orders.create"],
            [],
            MaximumRisk: 3,
            ["C:\\workspace"],
            ["C:\\workspace"],
            AllowOverwrite: false,
            AllowMove: false,
            AllowDelete: false,
            AllowedExecutables: [],
            AllowedEnvironmentVariables: [],
            AllowPowerShell: false,
            "{\"denyAll\":true,\"allowedHosts\":[]}",
            now.AddHours(1),
            AllowDelegation: true,
            ["agent-child"],
            [],
            now);

    private async Task<SqliteConnection> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        return connection;
    }

    private async Task DowngradeToVersionSevenAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE IF EXISTS meeting_invocations;
            DROP TABLE IF EXISTS meeting_participants;
            DROP TABLE IF EXISTS meeting_rounds;
            DROP TABLE IF EXISTS meeting_sessions;
            DROP TABLE IF EXISTS invocation_snapshots;
            DROP TABLE tool_audit;
            DROP TABLE tool_approvals;
            DROP TABLE tool_grants;
            DROP INDEX idx_tool_intents_status;
            DROP INDEX idx_tool_intents_run;
            ALTER TABLE tool_intents DROP COLUMN agent_id;
            ALTER TABLE tool_intents DROP COLUMN parent_agent_id;
            ALTER TABLE tool_intents DROP COLUMN catalog_version;
            ALTER TABLE tool_intents DROP COLUMN sent_at;
            ALTER TABLE tool_intents DROP COLUMN grant_id;
            ALTER TABLE tool_intents DROP COLUMN approval_request_id;
            ALTER TABLE tool_intents DROP COLUMN result_hash;
            ALTER TABLE tool_intents DROP COLUMN result_blob_id;
            ALTER TABLE tool_intents DROP COLUMN result_blob_length;
            ALTER TABLE tool_intents DROP COLUMN result_blob_sha256;
            ALTER TABLE tool_intents DROP COLUMN result_blob_content_type;
            ALTER TABLE tool_intents DROP COLUMN result_blob_access_scope;
            ALTER TABLE tool_intents DROP COLUMN result_blob_expires_at;
            ALTER TABLE tool_intents DROP COLUMN error_code;
            ALTER TABLE tool_intents DROP COLUMN result_visible;
            DELETE FROM schema_versions WHERE version > 7;
            """;
        await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
    }
}
