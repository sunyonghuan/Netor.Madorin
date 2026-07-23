using System.Globalization;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Tests;

[TestClass]
public sealed class SqliteMeetingRepositoryTests
{
    public TestContext TestContext { get; set; } = null!;

    #region Schema Migration

    [TestMethod]
    public async Task EnsureCreatedAsync_FromV9_PreservesOldDataAndReachesCurrentVersion()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        await DowngradeToV9Async(conn, TestContext.CancellationToken);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO sessions(session_id, mode, status, created_at, updated_at)
                VALUES('s-v9', 'Meeting', 'Active', '2026-07-22T00:00:00Z', '2026-07-22T00:00:00Z');
                INSERT INTO runs(run_id, session_id, status, run_sequence, created_at, updated_at)
                VALUES('r-v9', 's-v9', 'Running', 1, '2026-07-22T00:00:00Z', '2026-07-22T00:00:00Z');
                """;
            await cmd.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        await SqliteSchema.EnsureCreatedAsync(conn, TestContext.CancellationToken);

        Assert.AreEqual(
            SqliteSchema.CurrentVersion,
            await GetSchemaVersionAsync(conn, TestContext.CancellationToken));

        await using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sessions WHERE session_id = 's-v9';";
        Assert.AreEqual(
            1L,
            Convert.ToInt64(
                await check.ExecuteScalarAsync(TestContext.CancellationToken),
                CultureInfo.InvariantCulture));

        await using var runCheck = conn.CreateCommand();
        runCheck.CommandText = "SELECT status FROM runs WHERE run_id = 'r-v9';";
        Assert.AreEqual(
            "Running",
            await runCheck.ExecuteScalarAsync(TestContext.CancellationToken) as string);
    }

    [TestMethod]
    public async Task FreshDatabase_HasAllMeetingTablesIndexesAndKeyColumns()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);

        string[] expectedTables =
        [
            "meeting_sessions", "meeting_rounds",
            "meeting_participants", "meeting_invocations"
        ];
        foreach (var table in expectedTables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name;";
            cmd.Parameters.AddWithValue("$name", table);
            Assert.IsNotNull(
                await cmd.ExecuteScalarAsync(TestContext.CancellationToken),
                $"Table '{table}' missing.");
        }

        string[] expectedIndexes =
        [
            "idx_meeting_sessions_status",
            "idx_meeting_sessions_pending_approval",
            "idx_meeting_rounds_status",
            "idx_meeting_participants_join_order",
            "idx_meeting_invocations_scheduled",
            "idx_meeting_invocations_run_round",
            "idx_meeting_invocations_session_round_ordinal"
        ];
        foreach (var idx in expectedIndexes)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='index' AND name=$name;";
            cmd.Parameters.AddWithValue("$name", idx);
            Assert.IsNotNull(
                await cmd.ExecuteScalarAsync(TestContext.CancellationToken),
                $"Index '{idx}' missing.");
        }

        var keyColumns = new (string Table, string Column)[]
        {
            ("meeting_sessions", "selector_state"),
            ("meeting_sessions", "policy_json"),
            ("meeting_sessions", "pending_approval_request_id"),
            ("meeting_rounds", "first_invocation_id"),
            ("meeting_rounds", "summary_message_id"),
            ("meeting_participants", "join_order"),
            ("meeting_participants", "removed_at"),
            ("meeting_invocations", "ordinal"),
            ("meeting_invocations", "selector_decision_json"),
            ("meeting_invocations", "error_code")
        };
        foreach (var (table, column) in keyColumns)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT 1 FROM pragma_table_info($table) WHERE name = $column;
                """;
            cmd.Parameters.AddWithValue("$table", table);
            cmd.Parameters.AddWithValue("$column", column);
            Assert.IsNotNull(
                await cmd.ExecuteScalarAsync(TestContext.CancellationToken),
                $"Column '{table}.{column}' missing.");
        }

        Assert.AreEqual(
            SqliteSchema.CurrentVersion,
            await GetSchemaVersionAsync(conn, TestContext.CancellationToken));
    }

    #endregion

    #region Create Meeting, Round & First Scheduled Invocation

    [TestMethod]
    public async Task CreateMeetingSessionAndRound_ProducesCorrectState()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        var participants = new MeetingParticipantInput[]
        {
            new("p1", "agent-a", null, "Alice", 0),
            new("p2", "agent-b", null, "Bob", 1)
        };

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{\"mode\":\"round-robin\"}", "hash-1",
            participants, TestContext.CancellationToken);

        var firstInv = new MeetingInvocationScheduleInput(
            "inv-1", "p1", "agent-a", "speaker", 0);

        var scheduled = await repo.CreateRoundWithFirstInvocationAsync(
            sessionId, "run-1", 0, 1, firstInv, TestContext.CancellationToken);

        Assert.AreEqual("inv-1", scheduled.InvocationId);
        Assert.AreEqual("Scheduled", scheduled.Status);
        Assert.AreEqual(0, scheduled.Ordinal);
        Assert.AreEqual(1, scheduled.RoundIndex);

        var snapshot = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(1, snapshot.Session.CurrentRound);
        Assert.AreEqual("Running", snapshot.Session.Status);
        Assert.HasCount(2, snapshot.Participants);
        Assert.AreEqual("p1", snapshot.Participants[0].ParticipantId);
        Assert.AreEqual("p2", snapshot.Participants[1].ParticipantId);
        Assert.IsNotNull(snapshot.CurrentRound);
        Assert.AreEqual(1, snapshot.CurrentRound.RoundIndex);
        Assert.AreEqual("Running", snapshot.CurrentRound.Status);
        Assert.AreEqual("inv-1", snapshot.CurrentRound.FirstInvocationId);
    }

    [TestMethod]
    public async Task GetRunFirstRoundIndex_MultipleRuns_ReturnsEachRunMinimum()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await using (var seed = conn.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO meeting_rounds(
                    session_id, round_index, run_id, status, created_at)
                VALUES
                    ($sessionId, 1, 'run-1', 'Completed', $now),
                    ($sessionId, 2, 'run-1', 'Completed', $now),
                    ($sessionId, 3, 'run-2', 'Completed', $now),
                    ($sessionId, 4, 'run-2', 'Completed', $now);
                """;
            seed.Parameters.AddWithValue("$sessionId", sessionId);
            seed.Parameters.AddWithValue(
                "$now",
                DateTimeOffset.UtcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await seed.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        Assert.AreEqual(
            1,
            await repo.GetRunFirstRoundIndexAsync(
                sessionId,
                "run-1",
                TestContext.CancellationToken));
        Assert.AreEqual(
            3,
            await repo.GetRunFirstRoundIndexAsync(
                sessionId,
                "run-2",
                TestContext.CancellationToken));
        Assert.IsNull(await repo.GetRunFirstRoundIndexAsync(
            sessionId,
            "missing-run",
            TestContext.CancellationToken));
    }

    #endregion

    #region Complete Invocation & Next Scheduling

    [TestMethod]
    public async Task CompleteInvocation_WithNextSchedule_CreatesNextInSameTransaction()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h", DefaultParticipants(),
            TestContext.CancellationToken);
        await repo.CreateRoundWithFirstInvocationAsync(
            sessionId, "run-1", 0, 1,
            new("inv-1", "p1", "a1", "speaker", 0),
            TestContext.CancellationToken);

        await repo.TransitionInvocationStatusAsync(
            "inv-1", "Scheduled", "Running", TestContext.CancellationToken);

        var next = await repo.CompleteInvocationAsync(
            "inv-1", "Completed", "msg-1", "{\"decision\":\"ok\"}",
            null, null,
            new MeetingInvocationScheduleInput("inv-2", "p2", "a2", "speaker", 0),
            TestContext.CancellationToken);

        Assert.IsNotNull(next);
        Assert.AreEqual("inv-2", next.InvocationId);
        Assert.AreEqual("Scheduled", next.Status);
        Assert.AreEqual(1, next.Ordinal);

        var snap = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snap);
        Assert.IsNotNull(snap.NextScheduledInvocation);
        Assert.AreEqual("inv-2", snap.NextScheduledInvocation.InvocationId);
    }

    [TestMethod]
    public async Task CompleteInvocation_ExactScheduledReplay_ReturnsExistingRow()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h", DefaultParticipants(),
            TestContext.CancellationToken);
        await repo.CreateRoundWithFirstInvocationAsync(
            sessionId, "run-1", 0, 1,
            new("inv-1", "p1", "a1", "speaker", 0),
            TestContext.CancellationToken);

        await using (var pre = conn.CreateCommand())
        {
            pre.CommandText = """
                INSERT INTO meeting_invocations(
                    invocation_id, session_id, run_id, round_index, ordinal,
                    participant_id, agent_id, role, status,
                    selection_version, retry_count, scheduled_at)
                VALUES('inv-pre', $sessionId, 'run-1', 1, 1,
                       'p2', 'a2', 'speaker', 'Scheduled',
                       0, 0, '2026-07-23T00:00:00.0000000+00:00');
                """;
            pre.Parameters.AddWithValue("$sessionId", sessionId);
            await pre.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        await repo.TransitionInvocationStatusAsync(
            "inv-1", "Scheduled", "Running", TestContext.CancellationToken);

        var next = await repo.CompleteInvocationAsync(
            "inv-1", "Completed", "msg-1", null, null, null,
            new MeetingInvocationScheduleInput("inv-pre", "p2", "a2", "speaker", 0),
            TestContext.CancellationToken);

        Assert.IsNotNull(next);
        Assert.AreEqual("inv-pre", next.InvocationId);
        Assert.AreEqual(1, next.Ordinal);

        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = """
            SELECT COUNT(*) FROM meeting_invocations
            WHERE session_id = $sessionId AND round_index = 1 AND ordinal = 1;
            """;
        countCmd.Parameters.AddWithValue("$sessionId", sessionId);
        Assert.AreEqual(
            1L,
            Convert.ToInt64(
                await countCmd.ExecuteScalarAsync(TestContext.CancellationToken),
                CultureInfo.InvariantCulture));
    }

    #endregion

    #region Invocation Status Transitions

    [TestMethod]
    public async Task TransitionInvocationStatus_ValidTransition_Succeeds()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h", DefaultParticipants(),
            TestContext.CancellationToken);
        await repo.CreateRoundWithFirstInvocationAsync(
            sessionId, "run-1", 0, 1,
            new("inv-1", "p1", "a1", "speaker", 0),
            TestContext.CancellationToken);

        await repo.TransitionInvocationStatusAsync(
            "inv-1", "Scheduled", "Running", TestContext.CancellationToken);

        await repo.CompleteInvocationAsync(
            "inv-1", "Completed", "msg-1", null, null, null, null,
            TestContext.CancellationToken);

        var snap = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snap);
    }

    [TestMethod]
    public async Task TransitionInvocationStatus_InvalidTransition_Throws()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h", DefaultParticipants(),
            TestContext.CancellationToken);
        await repo.CreateRoundWithFirstInvocationAsync(
            sessionId, "run-1", 0, 1,
            new("inv-1", "p1", "a1", "speaker", 0),
            TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await repo.TransitionInvocationStatusAsync(
                "inv-1", "Scheduled", "Completed", TestContext.CancellationToken));
    }

    #endregion

    #region Selection Update & Optimistic Concurrency

    [TestMethod]
    public async Task TryUpdateSelection_Success_UpdatesPolicyAndParticipants()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h0",
            new[] { new MeetingParticipantInput("p1", "a1", null, "Alice", 0) },
            TestContext.CancellationToken);

        var newParticipants = new MeetingParticipantInput[]
        {
            new("p1", "a1", null, "Alice", 0),
            new("p2", "a2", null, "Bob", 1)
        };

        var result = await repo.TryUpdateSelectionAsync(
            sessionId, 0, 1, "{\"mode\":\" debate\"}", "h1",
            newParticipants, TestContext.CancellationToken);

        Assert.IsTrue(result);

        var snap = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snap);
        Assert.AreEqual(1, snap.Session.SelectionVersion);
        Assert.AreEqual("{\"mode\":\" debate\"}", snap.Session.PolicyJson);
        Assert.AreEqual("h1", snap.Session.PolicyHash);
        Assert.HasCount(2, snap.Participants);
    }

    [TestMethod]
    public async Task TryUpdateSelection_VersionConflict_NoChanges()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h0",
            new[] { new MeetingParticipantInput("p1", "a1", null, "Alice", 0) },
            TestContext.CancellationToken);

        var result = await repo.TryUpdateSelectionAsync(
            sessionId, 99, 100, "{\"new\":\"policy\"}", "h-new",
            new[] { new MeetingParticipantInput("p99", "a99", null, "Ghost", 0) },
            TestContext.CancellationToken);

        Assert.IsFalse(result);

        var snap = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snap);
        Assert.AreEqual(0, snap.Session.SelectionVersion);
        Assert.AreEqual("{}", snap.Session.PolicyJson);
        Assert.AreEqual("h0", snap.Session.PolicyHash);
        Assert.HasCount(1, snap.Participants);
        Assert.AreEqual("p1", snap.Participants[0].ParticipantId);
    }

    [TestMethod]
    public async Task TryUpdateSelection_ConcurrentSameExpectedVersion_OnlyOneCommitWins()
    {
        var dataDirectory = Path.Combine(
            TestContext.TestRunDirectory ?? Path.GetTempPath(),
            $"meeting-selection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "state.db"),
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();

        try
        {
            await using var setupConnection = new SqliteConnection(connectionString);
            await setupConnection.OpenAsync(TestContext.CancellationToken);
            await SqliteSchema.EnsureCreatedAsync(setupConnection, TestContext.CancellationToken);
            var setupRepository = new SqliteMeetingRepository(setupConnection);
            var sessionId = await SeedSessionAsync(
                setupConnection,
                TestContext.CancellationToken);
            await setupRepository.CreateMeetingSessionAsync(
                sessionId,
                "run-1",
                "{}",
                "h0",
                [new MeetingParticipantInput("p1", "a1", null, "Alice", 0)],
                TestContext.CancellationToken);

            await using var firstConnection = new SqliteConnection(connectionString);
            await using var secondConnection = new SqliteConnection(connectionString);
            await firstConnection.OpenAsync(TestContext.CancellationToken);
            await secondConnection.OpenAsync(TestContext.CancellationToken);
            var startGate = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<bool> UpdateAsync(
                SqliteConnection connection,
                string policyHash,
                MeetingParticipantInput addedParticipant)
            {
                await startGate.Task.WaitAsync(TestContext.CancellationToken);
                return await new SqliteMeetingRepository(connection).TryUpdateSelectionAsync(
                    sessionId,
                    expectedSelectionVersion: 0,
                    newSelectionVersion: 1,
                    $$"""{"winner":"{{policyHash}}"}""",
                    policyHash,
                    [new MeetingParticipantInput("p1", "a1", null, "Alice", 0), addedParticipant],
                    TestContext.CancellationToken);
            }

            var firstUpdate = UpdateAsync(
                firstConnection,
                "h-first",
                new MeetingParticipantInput("p-first", "a-first", null, "First", 1));
            var secondUpdate = UpdateAsync(
                secondConnection,
                "h-second",
                new MeetingParticipantInput("p-second", "a-second", null, "Second", 1));
            startGate.SetResult();
            var results = await Task.WhenAll(firstUpdate, secondUpdate);

            Assert.AreEqual(1, results.Count(static result => result));
            Assert.AreEqual(1, results.Count(static result => !result));

            var snapshot = await setupRepository.GetMeetingSnapshotAsync(
                sessionId,
                TestContext.CancellationToken);
            Assert.IsNotNull(snapshot);
            Assert.AreEqual(1, snapshot.Session.SelectionVersion);
            Assert.HasCount(2, snapshot.Participants);
            var winner = Assert.ContainsSingle(snapshot.Participants.Where(static participant =>
                participant.ParticipantId is "p-first" or "p-second"));
            Assert.AreEqual(
                winner.ParticipantId == "p-first" ? "h-first" : "h-second",
                snapshot.Session.PolicyHash);
            Assert.DoesNotContain(
                winner.ParticipantId == "p-first" ? "p-second" : "p-first",
                snapshot.Participants.Select(static participant => participant.ParticipantId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    #endregion

    #region Participant Lifecycle

    [TestMethod]
    public async Task ParticipantAddStandbyRemoveReorder_DeterministicReadAndSoftDelete()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h0",
            new[]
            {
                new MeetingParticipantInput("p1", "a1", null, "Alice", 0),
                new MeetingParticipantInput("p2", "a2", null, "Bob", 1),
                new MeetingParticipantInput("p3", "a3", null, "Carol", 2)
            },
            TestContext.CancellationToken);

        var result = await repo.TryUpdateSelectionAsync(
            sessionId, 0, 1, "{}", "h1",
            new[]
            {
                new MeetingParticipantInput("p3", "a3", null, "Carol", 0),
                new MeetingParticipantInput("p1", "a1", null, "Alice", 1),
                new MeetingParticipantInput("p4", "a4", null, "Dave", 2)
            },
            TestContext.CancellationToken);
        Assert.IsTrue(result);

        var snap = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snap);
        Assert.AreEqual(1, snap.Session.SelectionVersion);

        Assert.HasCount(4, snap.Participants);
        Assert.AreEqual("p3", snap.Participants[0].ParticipantId);
        Assert.AreEqual(0, snap.Participants[0].JoinOrder);
        Assert.AreEqual("active", snap.Participants[0].Status);

        Assert.AreEqual("p1", snap.Participants[1].ParticipantId);
        Assert.AreEqual(1, snap.Participants[1].JoinOrder);
        Assert.AreEqual("active", snap.Participants[1].Status);

        Assert.AreEqual("p4", snap.Participants[2].ParticipantId);
        Assert.AreEqual(2, snap.Participants[2].JoinOrder);
        Assert.AreEqual("active", snap.Participants[2].Status);

        Assert.AreEqual("p2", snap.Participants[3].ParticipantId);
        Assert.AreEqual("removed", snap.Participants[3].Status);
        Assert.AreEqual(1, snap.Participants[3].RemovedSelectionVersion);
        Assert.IsNotNull(snap.Participants[3].RemovedAt);
    }

    #endregion

    #region Pending Approval

    [TestMethod]
    public async Task PendingApproval_IdempotentSaveResolveAndRecovery()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h", DefaultParticipants(),
            TestContext.CancellationToken);

        // Save initial pending approval
        var saved1 = await repo.SavePendingApprovalAsync(
            sessionId, "appr-1", "{\"tool\":\"exec\"}", "Pending",
            TestContext.CancellationToken);
        Assert.IsTrue(saved1);

        // Identical replay succeeds
        var saved2 = await repo.SavePendingApprovalAsync(
            sessionId, "appr-1", "{\"tool\":\"exec\"}", "Pending",
            TestContext.CancellationToken);
        Assert.IsTrue(saved2);

        var snap = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snap);
        Assert.AreEqual("appr-1", snap.Session.PendingApprovalRequestId);
        Assert.AreEqual("{\"tool\":\"exec\"}", snap.Session.PendingApprovalJson);
        Assert.AreEqual("Pending", snap.Session.PendingApprovalStatus);

        // Conflicting different active request throws
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await repo.SavePendingApprovalAsync(
                sessionId, "appr-conflict", "{\"tool\":\"other\"}", "Pending",
                TestContext.CancellationToken));

        await repo.ResolvePendingApprovalAsync(
            sessionId, "appr-1", "{\"decision\":\"approved\"}", "Decided",
            TestContext.CancellationToken);

        snap = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snap);
        Assert.AreEqual("appr-1", snap.Session.PendingApprovalRequestId);
        Assert.AreEqual("{\"decision\":\"approved\"}", snap.PendingApprovalJson);
        Assert.AreEqual("Decided", snap.PendingApprovalStatus);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await repo.ResolvePendingApprovalAsync(
                sessionId, "appr-wrong", "{\"decision\":\"nope\"}", "Decided",
                TestContext.CancellationToken));

        await repo.ResolvePendingApprovalAsync(
            sessionId, "appr-1", "{\"decision\":\"approved\"}", "Decided",
            TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await repo.ResolvePendingApprovalAsync(
                sessionId, "appr-999", "{\"decision\":\"x\"}", "Decided",
                TestContext.CancellationToken));

        var staleResult = await repo.SavePendingApprovalAsync(
            sessionId, "appr-1", "{\"decision\":\"approved\"}", "Decided",
            TestContext.CancellationToken);
        Assert.IsTrue(staleResult);

        snap = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snap);
        Assert.AreEqual("Decided", snap.PendingApprovalStatus);

        var newResult = await repo.SavePendingApprovalAsync(
            sessionId, "appr-2", "{\"tool\":\"rm\"}", "Pending",
            TestContext.CancellationToken);
        Assert.IsTrue(newResult);

        snap = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snap);
        Assert.AreEqual("appr-2", snap.Session.PendingApprovalRequestId);
        Assert.AreEqual("Pending", snap.Session.PendingApprovalStatus);
    }

    [TestMethod]
    public async Task FindApprovalByRequestId_ReturnsPendingRecordAndNullForUnknown()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h", DefaultParticipants(),
            TestContext.CancellationToken);

        await repo.SavePendingApprovalAsync(
            sessionId, "appr-find", "{\"tool\":\"exec\"}", "Pending",
            TestContext.CancellationToken);

        var record = await repo.FindApprovalByRequestIdAsync(
            "appr-find", TestContext.CancellationToken);
        Assert.IsNotNull(record);
        Assert.AreEqual(sessionId, record.SessionId);
        Assert.AreEqual("run-1", record.RunId);
        Assert.AreEqual("appr-find", record.ApprovalRequestId);
        Assert.AreEqual("{\"tool\":\"exec\"}", record.ApprovalJson);
        Assert.AreEqual("Pending", record.Status);

        var missing = await repo.FindApprovalByRequestIdAsync(
            "unknown-id", TestContext.CancellationToken);
        Assert.IsNull(missing);
    }

    [TestMethod]
    public async Task ListRecoverableApprovals_ReturnsOnlyWaitingPendingOrDecidedInUpdateOrder()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var pendingSessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);
        var decidedSessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);
        var runningSessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);
        var completedSessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);
        var missingRequestSessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);
        var invalidStatusSessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        var sessions = new[]
        {
            (pendingSessionId, "run-pending"),
            (decidedSessionId, "run-decided"),
            (runningSessionId, "run-running"),
            (completedSessionId, "run-completed"),
            (missingRequestSessionId, "run-missing-request"),
            (invalidStatusSessionId, "run-invalid-status")
        };
        foreach (var (sessionId, runId) in sessions)
        {
            await repo.CreateMeetingSessionAsync(
                sessionId,
                runId,
                "{}",
                "h",
                DefaultParticipants(),
                TestContext.CancellationToken);
        }

        await repo.SavePendingApprovalAsync(
            pendingSessionId,
            "approval-pending",
            "{\"kind\":\"pending\"}",
            "Pending",
            TestContext.CancellationToken);
        await repo.TryTransitionMeetingStatusAsync(
            pendingSessionId,
            "Running",
            "WaitingForApproval",
            TestContext.CancellationToken);

        await repo.SavePendingApprovalAsync(
            decidedSessionId,
            "approval-decided",
            "{\"kind\":\"pending\"}",
            "Pending",
            TestContext.CancellationToken);
        await repo.ResolvePendingApprovalAsync(
            decidedSessionId,
            "approval-decided",
            "{\"decision\":\"approved\"}",
            "Decided",
            TestContext.CancellationToken);
        await repo.TryTransitionMeetingStatusAsync(
            decidedSessionId,
            "Running",
            "WaitingForApproval",
            TestContext.CancellationToken);

        await repo.SavePendingApprovalAsync(
            runningSessionId,
            "approval-running",
            "{}",
            "Pending",
            TestContext.CancellationToken);
        await repo.SavePendingApprovalAsync(
            completedSessionId,
            "approval-completed",
            "{}",
            "Pending",
            TestContext.CancellationToken);
        await repo.TryTransitionMeetingStatusAsync(
            completedSessionId,
            "Running",
            "Completed",
            TestContext.CancellationToken);
        await repo.TryTransitionMeetingStatusAsync(
            missingRequestSessionId,
            "Running",
            "WaitingForApproval",
            TestContext.CancellationToken);
        await repo.SavePendingApprovalAsync(
            invalidStatusSessionId,
            "approval-invalid",
            "{}",
            "Expired",
            TestContext.CancellationToken);
        await repo.TryTransitionMeetingStatusAsync(
            invalidStatusSessionId,
            "Running",
            "WaitingForApproval",
            TestContext.CancellationToken);

        await using (var updateOrder = conn.CreateCommand())
        {
            updateOrder.CommandText = """
                UPDATE meeting_sessions
                SET updated_at = '2026-07-23T01:00:00.0000000+00:00'
                WHERE session_id = $decidedSessionId;

                UPDATE meeting_sessions
                SET updated_at = '2026-07-23T02:00:00.0000000+00:00'
                WHERE session_id = $pendingSessionId;
                """;
            updateOrder.Parameters.AddWithValue("$decidedSessionId", decidedSessionId);
            updateOrder.Parameters.AddWithValue("$pendingSessionId", pendingSessionId);
            await updateOrder.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        var approvals = await repo.ListRecoverableApprovalsAsync(
            TestContext.CancellationToken);

        Assert.HasCount(2, approvals);
        Assert.AreEqual(decidedSessionId, approvals[0].SessionId);
        Assert.AreEqual("run-decided", approvals[0].RunId);
        Assert.AreEqual("approval-decided", approvals[0].ApprovalRequestId);
        Assert.AreEqual("{\"decision\":\"approved\"}", approvals[0].ApprovalJson);
        Assert.AreEqual("Decided", approvals[0].Status);
        Assert.AreEqual(pendingSessionId, approvals[1].SessionId);
        Assert.AreEqual("run-pending", approvals[1].RunId);
        Assert.AreEqual("approval-pending", approvals[1].ApprovalRequestId);
        Assert.AreEqual("{\"kind\":\"pending\"}", approvals[1].ApprovalJson);
        Assert.AreEqual("Pending", approvals[1].Status);
    }

    [TestMethod]
    public async Task TryTransitionMeetingStatus_UsesExpectedStatusAndIsIdempotent()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h", DefaultParticipants(),
            TestContext.CancellationToken);

        var transitioned = await repo.TryTransitionMeetingStatusAsync(
            sessionId, "Running", "WaitingForApproval", TestContext.CancellationToken);
        Assert.IsTrue(transitioned);

        var snap = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snap);
        Assert.AreEqual("WaitingForApproval", snap.Session.Status);

        var idempotent = await repo.TryTransitionMeetingStatusAsync(
            sessionId, "WaitingForApproval", "WaitingForApproval", TestContext.CancellationToken);
        Assert.IsTrue(idempotent);

        snap = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snap);
        Assert.AreEqual("WaitingForApproval", snap.Session.Status);

        var wrongExpected = await repo.TryTransitionMeetingStatusAsync(
            sessionId, "Running", "Completed", TestContext.CancellationToken);
        Assert.IsFalse(wrongExpected);

        snap = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snap);
        Assert.AreEqual("WaitingForApproval", snap.Session.Status);

        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(
            async () => await repo.TryTransitionMeetingStatusAsync(
                "nonexistent-session", "Running", "Completed",
                TestContext.CancellationToken));
    }

    #endregion

    #region Summary Cache

    [TestMethod]
    public async Task SaveRoundSummary_IdempotentAndLatestSummary()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h", DefaultParticipants(),
            TestContext.CancellationToken);
        await repo.CreateRoundWithFirstInvocationAsync(
            sessionId, "run-1", 0, 1,
            new("inv-1", "p1", "a1", "speaker", 0),
            TestContext.CancellationToken);

        await repo.SaveRoundSummaryAsync(
            sessionId, 1, 10, "policy-hash-1", "msg-sum-1",
            "a1", "inv-1", TestContext.CancellationToken);

        await repo.SaveRoundSummaryAsync(
            sessionId, 1, 10, "policy-hash-1", "msg-sum-1",
            "a1", "inv-1", TestContext.CancellationToken);

        var snap = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snap);
        Assert.IsNotNull(snap.LatestSummary);
        Assert.AreEqual("msg-sum-1", snap.LatestSummary.SummaryMessageId);
        Assert.AreEqual(10, snap.LatestSummary.SummarizesThroughSeq);
        Assert.AreEqual("policy-hash-1", snap.LatestSummary.SummaryPolicyHash);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await repo.SaveRoundSummaryAsync(
                sessionId, 1, 20, "policy-hash-2", "msg-sum-2",
                "a1", "inv-1", TestContext.CancellationToken));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await repo.SaveRoundSummaryAsync(
                sessionId, 1, 20, "policy-hash-2", "msg-sum-1",
                "a1", "inv-1", TestContext.CancellationToken));

        snap = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snap);
        Assert.IsNotNull(snap.LatestSummary);
        Assert.AreEqual(10, snap.LatestSummary.SummarizesThroughSeq);
        Assert.AreEqual("policy-hash-1", snap.LatestSummary.SummaryPolicyHash);
    }

    #endregion

    #region Recovery

    [TestMethod]
    public async Task RecoverRunningInvocations_OnlyConvertsRunning()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h", DefaultParticipants(),
            TestContext.CancellationToken);
        await repo.CreateRoundWithFirstInvocationAsync(
            sessionId, "run-1", 0, 1,
            new("inv-1", "p1", "a1", "speaker", 0),
            TestContext.CancellationToken);

        await repo.TransitionInvocationStatusAsync(
            "inv-1", "Scheduled", "Running", TestContext.CancellationToken);

        var nextInv = await repo.CompleteInvocationAsync(
            "inv-1", "Completed", "msg-1", null, null, null,
            new MeetingInvocationScheduleInput("inv-2", "p2", "a2", "speaker", 0),
            TestContext.CancellationToken);
        Assert.IsNotNull(nextInv);

        await repo.TransitionInvocationStatusAsync(
            "inv-2", "Scheduled", "Running", TestContext.CancellationToken);

        var completed = await repo.CompleteInvocationAsync(
            "inv-2", "Completed", "msg-2", null, null, null,
            new MeetingInvocationScheduleInput("inv-3", "p1", "a1", "speaker", 0),
            TestContext.CancellationToken);
        Assert.IsNotNull(completed);

        var recovered = await repo.RecoverRunningInvocationsAsync(
            TestContext.CancellationToken);
        Assert.HasCount(0, recovered);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE meeting_invocations SET status = 'Running'
                WHERE invocation_id = 'inv-3';
                """;
            await cmd.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        recovered = await repo.RecoverRunningInvocationsAsync(
            TestContext.CancellationToken);
        Assert.HasCount(1, recovered);
        Assert.AreEqual("inv-3", recovered[0].InvocationId);
        Assert.AreEqual("Interrupted", recovered[0].Status);

        var recoverable = await repo.QueryRecoverableInvocationsAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsTrue(recoverable.Any(i => i.InvocationId == "inv-3" && i.Status == "Interrupted"));

        var snapshot = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snapshot);
        Assert.IsNotNull(snapshot.NextScheduledInvocation);
        Assert.AreEqual("inv-3", snapshot.NextScheduledInvocation.InvocationId);
        Assert.AreEqual("Interrupted", snapshot.NextScheduledInvocation.Status);
        Assert.AreEqual("speaker", recoverable[0].Role);
        Assert.AreEqual(0, recoverable[0].SelectionVersion);
        Assert.AreEqual(0, recoverable[0].RetryCount);
    }

    #endregion

    #region Session Snapshot

    [TestMethod]
    public async Task MeetingSnapshot_AllFieldsConsistent()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{\"mode\":\"debate\"}", "policy-h",
            new[]
            {
                new MeetingParticipantInput("p1", "a1", "{\"ref\":1}", "Alice", 0),
                new MeetingParticipantInput("p2", "a2", null, "Bob", 1)
            },
            TestContext.CancellationToken);

        var inv = await repo.CreateRoundWithFirstInvocationAsync(
            sessionId, "run-1", 0, 1,
            new MeetingInvocationScheduleInput("inv-1", "p1", "a1", "speaker", 0),
            TestContext.CancellationToken);

        await repo.SavePendingApprovalAsync(
            sessionId, "appr-1", "{\"approval\":\"data\"}", "Pending",
            TestContext.CancellationToken);

        var snap = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snap);

        Assert.AreEqual(sessionId, snap.Session.SessionId);
        Assert.AreEqual("run-1", snap.Session.RunId);
        Assert.AreEqual("Running", snap.Session.Status);
        Assert.AreEqual(1, snap.Session.CurrentRound);
        Assert.AreEqual(0, snap.Session.SelectionVersion);
        Assert.AreEqual("{\"mode\":\"debate\"}", snap.Session.PolicyJson);
        Assert.AreEqual("policy-h", snap.Session.PolicyHash);

        Assert.HasCount(2, snap.Participants);
        Assert.AreEqual("p1", snap.Participants[0].ParticipantId);
        Assert.AreEqual("a1", snap.Participants[0].AgentId);
        Assert.AreEqual("{\"ref\":1}", snap.Participants[0].AgentRefJson);
        Assert.AreEqual("Alice", snap.Participants[0].DisplayName);
        Assert.AreEqual(0, snap.Participants[0].JoinOrder);
        Assert.AreEqual("active", snap.Participants[0].Status);
        Assert.AreEqual(0, snap.Participants[0].JoinedSelectionVersion);

        Assert.IsNotNull(snap.CurrentRound);
        Assert.AreEqual(1, snap.CurrentRound.RoundIndex);
        Assert.AreEqual("Running", snap.CurrentRound.Status);
        Assert.AreEqual("inv-1", snap.CurrentRound.FirstInvocationId);

        Assert.IsNotNull(snap.NextScheduledInvocation);
        Assert.AreEqual("inv-1", snap.NextScheduledInvocation.InvocationId);
        Assert.AreEqual("Scheduled", snap.NextScheduledInvocation.Status);
        Assert.AreEqual(0, snap.NextScheduledInvocation.Ordinal);
        Assert.AreEqual(1, snap.NextScheduledInvocation.RoundIndex);

        Assert.AreEqual("{\"approval\":\"data\"}", snap.PendingApprovalJson);
        Assert.AreEqual("Pending", snap.PendingApprovalStatus);

        Assert.IsNull(snap.LatestSummary);
    }

    [TestMethod]
    public async Task GetMeetingSnapshot_NonExistentSession_ReturnsNull()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);

        var snap = await repo.GetMeetingSnapshotAsync(
            "nonexistent", TestContext.CancellationToken);
        Assert.IsNull(snap);
    }

    #endregion

    #region Reconciliation

    [TestMethod]
    public async Task ReconcileByMessageId_ReturnsMatchingInvocations()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h", DefaultParticipants(),
            TestContext.CancellationToken);
        await repo.CreateRoundWithFirstInvocationAsync(
            sessionId, "run-1", 0, 1,
            new("inv-1", "p1", "a1", "speaker", 0),
            TestContext.CancellationToken);

        await repo.TransitionInvocationStatusAsync(
            "inv-1", "Scheduled", "Running", TestContext.CancellationToken);
        await repo.CompleteInvocationAsync(
            "inv-1", "Completed", "msg-target", null, null, null, null,
            TestContext.CancellationToken);

        var items = await repo.ReconcileByMessageIdAsync(
            "msg-target", TestContext.CancellationToken);
        Assert.HasCount(1, items);
        Assert.AreEqual("inv-1", items[0].InvocationId);
        Assert.AreEqual("msg-target", items[0].MessageId);
        Assert.AreEqual("Completed", items[0].Status);

        var empty = await repo.ReconcileByMessageIdAsync(
            "msg-nonexistent", TestContext.CancellationToken);
        Assert.HasCount(0, empty);
    }

    [TestMethod]
    public async Task ReconcileInterruptedInvocation_CanonicalMessage_CompletesIdempotently()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h", DefaultParticipants(),
            TestContext.CancellationToken);
        await repo.CreateRoundWithFirstInvocationAsync(
            sessionId, "run-1", 0, 1,
            new("inv-reconcile", "p1", "a1", "participant", 3),
            TestContext.CancellationToken);
        await repo.TransitionInvocationStatusAsync(
            "inv-reconcile", "Scheduled", "Running", TestContext.CancellationToken);
        await repo.RecoverRunningInvocationsAsync(TestContext.CancellationToken);

        var reconciled = await repo.ReconcileInterruptedInvocationAsync(
            "inv-reconcile", "message-canonical", TestContext.CancellationToken);
        var replayed = await repo.ReconcileInterruptedInvocationAsync(
            "inv-reconcile", "message-canonical", TestContext.CancellationToken);

        Assert.AreEqual("Completed", reconciled.Status);
        Assert.AreEqual("message-canonical", reconciled.MessageId);
        Assert.AreEqual(reconciled, replayed);
        Assert.IsEmpty(await repo.QueryRecoverableInvocationsAsync(
            sessionId, TestContext.CancellationToken));

        var byMessage = await repo.ReconcileByMessageIdAsync(
            "message-canonical", TestContext.CancellationToken);
        Assert.HasCount(1, byMessage);
        Assert.AreEqual("inv-reconcile", byMessage[0].InvocationId);
    }

    [TestMethod]
    public async Task ReconcileInterruptedInvocation_DifferentMessage_RejectsWithoutMutation()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h", DefaultParticipants(),
            TestContext.CancellationToken);
        await repo.CreateRoundWithFirstInvocationAsync(
            sessionId, "run-1", 0, 1,
            new("inv-conflict", "p1", "a1", "participant", 0),
            TestContext.CancellationToken);
        await repo.TransitionInvocationStatusAsync(
            "inv-conflict", "Scheduled", "Running", TestContext.CancellationToken);
        await repo.RecoverRunningInvocationsAsync(TestContext.CancellationToken);
        await repo.ReconcileInterruptedInvocationAsync(
            "inv-conflict", "message-original", TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await repo.ReconcileInterruptedInvocationAsync(
                "inv-conflict", "message-conflict", TestContext.CancellationToken));

        var original = await repo.ReconcileByMessageIdAsync(
            "message-original", TestContext.CancellationToken);
        var conflict = await repo.ReconcileByMessageIdAsync(
            "message-conflict", TestContext.CancellationToken);
        Assert.HasCount(1, original);
        Assert.AreEqual("Completed", original[0].Status);
        Assert.IsEmpty(conflict);
    }

    [TestMethod]
    public async Task ReconcileInterruptedSummary_RestoresInvocationAndSummaryAtomically()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "policy-hash", DefaultParticipants(),
            TestContext.CancellationToken);
        await repo.CreateRoundWithFirstInvocationAsync(
            sessionId, "run-1", 0, 1,
            new("inv-summary", null, "meeting.summarizer", "summarizer", 2),
            TestContext.CancellationToken);
        await repo.TransitionInvocationStatusAsync(
            "inv-summary", "Scheduled", "Running", TestContext.CancellationToken);
        await repo.RecoverRunningInvocationsAsync(TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await repo.ReconcileInterruptedSummaryAsync(
                "inv-summary",
                "message-summary",
                7,
                "policy-hash",
                "wrong-agent",
                TestContext.CancellationToken));

        var stillRecoverable = await repo.QueryRecoverableInvocationsAsync(
            sessionId, TestContext.CancellationToken);
        Assert.HasCount(1, stillRecoverable);
        Assert.AreEqual("Interrupted", stillRecoverable[0].Status);
        Assert.IsNull((await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken))?.LatestSummary);

        var reconciled = await repo.ReconcileInterruptedSummaryAsync(
            "inv-summary",
            "message-summary",
            7,
            "policy-hash",
            "meeting.summarizer",
            TestContext.CancellationToken);
        var replayed = await repo.ReconcileInterruptedSummaryAsync(
            "inv-summary",
            "message-summary",
            7,
            "policy-hash",
            "meeting.summarizer",
            TestContext.CancellationToken);

        Assert.AreEqual("Completed", reconciled.Status);
        Assert.AreEqual(reconciled, replayed);
        var snapshot = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snapshot);
        Assert.IsNotNull(snapshot.LatestSummary);
        Assert.AreEqual("message-summary", snapshot.LatestSummary.SummaryMessageId);
        Assert.AreEqual(7, snapshot.LatestSummary.SummarizesThroughSeq);
        Assert.AreEqual("policy-hash", snapshot.LatestSummary.SummaryPolicyHash);
        Assert.AreEqual("meeting.summarizer", snapshot.LatestSummary.SummaryAgentId);
        Assert.AreEqual("inv-summary", snapshot.LatestSummary.SummaryInvocationId);
    }

    #endregion

    #region Helpers

    private static async Task<SqliteConnection> CreateFullDatabaseAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync(ct);
        await SqliteSchema.EnsureCreatedAsync(conn, ct);
        return conn;
    }

    private static async Task<string> SeedSessionAsync(
        SqliteConnection conn, CancellationToken ct)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions(session_id, mode, status, created_at, updated_at)
            VALUES($sessionId, 'Meeting', 'Active', $now, $now);
            """;
        cmd.Parameters.AddWithValue("$sessionId", sessionId);
        cmd.Parameters.AddWithValue("$now",
            DateTimeOffset.UtcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
        return sessionId;
    }

    private static MeetingParticipantInput[] DefaultParticipants() =>
    [
        new("p1", "a1", null, "Alice", 0),
        new("p2", "a2", null, "Bob", 1)
    ];

    private static async Task DowngradeToV9Async(
        SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DROP TABLE IF EXISTS meeting_invocations;
            DROP TABLE IF EXISTS meeting_participants;
            DROP TABLE IF EXISTS meeting_rounds;
            DROP TABLE IF EXISTS meeting_sessions;
            DELETE FROM schema_versions WHERE version > 9;
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<int> GetSchemaVersionAsync(
        SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(version) FROM schema_versions;";
        return Convert.ToInt32(
            await cmd.ExecuteScalarAsync(ct),
            CultureInfo.InvariantCulture);
    }

    #endregion
}
