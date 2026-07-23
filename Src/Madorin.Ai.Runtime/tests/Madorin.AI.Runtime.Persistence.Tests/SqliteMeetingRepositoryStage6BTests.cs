using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Tests;

[TestClass]
public sealed class SqliteMeetingRepositoryStage6BTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CreateMeetingSession_DuplicateRequest_DoesNotMutateExistingSelection()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        var participants1 = new[]
        {
            new MeetingParticipantInput("p1", "a1", null, "Alice", 0)
        };

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{\"mode\":\"round-robin\"}", "hash-1",
            participants1, TestContext.CancellationToken);

        var participants2 = new[]
        {
            new MeetingParticipantInput("p2", "a2", null, "Bob", 0)
        };

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-2", "{\"mode\":\"debate\"}", "hash-2",
            participants2, TestContext.CancellationToken);

        var snap = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snap);
        Assert.AreEqual("run-1", snap.Session.RunId);
        Assert.AreEqual("{\"mode\":\"round-robin\"}", snap.Session.PolicyJson);
        Assert.AreEqual("hash-1", snap.Session.PolicyHash);
        Assert.AreEqual(0, snap.Session.SelectionVersion);
        Assert.HasCount(1, snap.Participants);
        Assert.AreEqual("p1", snap.Participants[0].ParticipantId);
    }

    [TestMethod]
    [DataRow("duplicateId")]
    [DataRow("duplicateJoinOrder")]
    [DataRow("blankParticipantId")]
    [DataRow("blankAgentId")]
    [DataRow("invalidStatus")]
    public async Task CreateMeetingSession_InvalidParticipantInput_RejectsBeforeMutation(string caseName)
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        MeetingParticipantInput[] participants = caseName switch
        {
            "duplicateId" =>
            [
                new("p1", "a1", null, "Alice", 0),
                new("p1", "a2", null, "Bob", 1)
            ],
            "duplicateJoinOrder" =>
            [
                new("p1", "a1", null, "Alice", 0),
                new("p2", "a2", null, "Bob", 0)
            ],
            "blankParticipantId" =>
            [
                new("", "a1", null, "Alice", 0)
            ],
            "blankAgentId" =>
            [
                new("p1", "", null, "Alice", 0)
            ],
            "invalidStatus" =>
            [
                new("p1", "a1", null, "Alice", 0, "unknown")
            ],
            _ => throw new ArgumentOutOfRangeException(caseName)
        };

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            async () => await repo.CreateMeetingSessionAsync(
                sessionId, "run-1", "{}", "h",
                participants, TestContext.CancellationToken));

        var snap = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNull(snap);
    }

    [TestMethod]
    public async Task ParticipantStatus_Standby_RoundTripsOnCreateAndSelectionUpdate()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h0",
            new[]
            {
                new MeetingParticipantInput("p1", "a1", null, "Alice", 0, "standby")
            },
            TestContext.CancellationToken);

        var snap0 = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snap0);
        Assert.HasCount(1, snap0.Participants);
        Assert.AreEqual("standby", snap0.Participants[0].Status);

        var updated = await repo.TryUpdateSelectionAsync(
            sessionId, 0, 1, "{}", "h1",
            new[]
            {
                new MeetingParticipantInput("p1", "a1", null, "Alice", 1, "standby"),
                new MeetingParticipantInput("p2", "a2", null, "Bob", 0, "active")
            },
            TestContext.CancellationToken);
        Assert.IsTrue(updated);

        var snap1 = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snap1);
        Assert.HasCount(2, snap1.Participants);
        Assert.AreEqual("p2", snap1.Participants[0].ParticipantId);
        Assert.AreEqual("active", snap1.Participants[0].Status);
        Assert.AreEqual("p1", snap1.Participants[1].ParticipantId);
        Assert.AreEqual("standby", snap1.Participants[1].Status);
    }

    [TestMethod]
    [DataRow("Participant", "participant")]
    [DataRow("SELECTOR", "selector")]
    [DataRow("host", "host")]
    [DataRow("SuMmArIzEr", "summarizer")]
    public async Task GetInvocationContextMap_KnownRole_NormalizesCase(
        string persistedRole,
        string expectedRole)
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId,
            "run-1",
            "{}",
            "h",
            [new MeetingParticipantInput("p1", "a1", null, "Alice", 0)],
            TestContext.CancellationToken);
        await repo.CreateRoundWithFirstInvocationAsync(
            sessionId,
            "run-1",
            0,
            1,
            new MeetingInvocationScheduleInput("inv-1", "p1", "a1", persistedRole, 0),
            TestContext.CancellationToken);

        var contextMap = await repo.GetInvocationContextMapAsync(
            sessionId,
            TestContext.CancellationToken);

        Assert.HasCount(1, contextMap);
        Assert.AreEqual(1, contextMap["inv-1"].RoundIndex);
        Assert.AreEqual(expectedRole, contextMap["inv-1"].Role);
    }

    [TestMethod]
    public async Task GetInvocationContextMap_UnknownRole_ThrowsDeterministicError()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId,
            "run-1",
            "{}",
            "h",
            [new MeetingParticipantInput("p1", "a1", null, "Alice", 0)],
            TestContext.CancellationToken);
        await repo.CreateRoundWithFirstInvocationAsync(
            sessionId,
            "run-1",
            0,
            1,
            new MeetingInvocationScheduleInput("inv-unknown", "p1", "a1", "observer", 0),
            TestContext.CancellationToken);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await repo.GetInvocationContextMapAsync(
                sessionId,
                TestContext.CancellationToken));

        StringAssert.Contains(exception.Message, "inv-unknown");
        StringAssert.Contains(exception.Message, "observer");
    }

    [TestMethod]
    [DataRow("Completed")]
    [DataRow("Skipped")]
    [DataRow("Failed")]
    public async Task CompleteInvocation_RunningTerminalState_PersistsStatus(string terminalStatus)
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h",
            new[] { new MeetingParticipantInput("p1", "a1", null, "Alice", 0) },
            TestContext.CancellationToken);
        await repo.CreateRoundWithFirstInvocationAsync(
            sessionId, "run-1", 0, 1,
            new("inv-1", "p1", "a1", "speaker", 0),
            TestContext.CancellationToken);

        await repo.TransitionInvocationStatusAsync(
            "inv-1", "Scheduled", "Running", TestContext.CancellationToken);

        await repo.CompleteInvocationAsync(
            "inv-1", terminalStatus, null, null, null, null, null,
            TestContext.CancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT status FROM meeting_invocations WHERE invocation_id = 'inv-1';
            """;
        var status = await cmd.ExecuteScalarAsync(TestContext.CancellationToken) as string;
        Assert.AreEqual(terminalStatus, status);
    }

    [TestMethod]
    public async Task CompleteInvocation_ScheduledToInterrupted_Succeeds()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h",
            new[] { new MeetingParticipantInput("p1", "a1", null, "Alice", 0) },
            TestContext.CancellationToken);
        await repo.CreateRoundWithFirstInvocationAsync(
            sessionId, "run-1", 0, 1,
            new("inv-1", "p1", "a1", "speaker", 0),
            TestContext.CancellationToken);

        await repo.CompleteInvocationAsync(
            "inv-1", "Interrupted", null, null, null, null, null,
            TestContext.CancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT status FROM meeting_invocations WHERE invocation_id = 'inv-1';
            """;
        var status = await cmd.ExecuteScalarAsync(TestContext.CancellationToken) as string;
        Assert.AreEqual("Interrupted", status);
    }

    [TestMethod]
    public async Task CompleteInvocation_ScheduledToCompleted_ThrowsAndPreservesScheduled()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h",
            new[] { new MeetingParticipantInput("p1", "a1", null, "Alice", 0) },
            TestContext.CancellationToken);
        await repo.CreateRoundWithFirstInvocationAsync(
            sessionId, "run-1", 0, 1,
            new("inv-1", "p1", "a1", "speaker", 0),
            TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await repo.CompleteInvocationAsync(
                "inv-1", "Completed", null, null, null, null, null,
                TestContext.CancellationToken));

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT status FROM meeting_invocations WHERE invocation_id = 'inv-1';
            """;
        var status = await cmd.ExecuteScalarAsync(TestContext.CancellationToken) as string;
        Assert.AreEqual("Scheduled", status);
    }

    [TestMethod]
    public async Task CompleteInvocation_ConflictingInvocationOwnership_RollsBack()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionA = await SeedSessionAsync(conn, TestContext.CancellationToken);
        var sessionB = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionA, "run-a", "{}", "h-a",
            new[] { new MeetingParticipantInput("p1", "a1", null, "Alice", 0) },
            TestContext.CancellationToken);
        await repo.CreateMeetingSessionAsync(
            sessionB, "run-b", "{}", "h-b",
            new[] { new MeetingParticipantInput("p2", "a2", null, "Bob", 0) },
            TestContext.CancellationToken);

        await repo.CreateRoundWithFirstInvocationAsync(
            sessionB, "run-b", 0, 1,
            new("inv-conflict", "p2", "a2", "speaker", 0),
            TestContext.CancellationToken);

        await repo.CreateRoundWithFirstInvocationAsync(
            sessionA, "run-a", 0, 1,
            new("inv-a", "p1", "a1", "speaker", 0),
            TestContext.CancellationToken);

        await repo.TransitionInvocationStatusAsync(
            "inv-a", "Scheduled", "Running", TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await repo.CompleteInvocationAsync(
                "inv-a", "Completed", "msg-1", null, null, null,
                new MeetingInvocationScheduleInput(
                    "inv-conflict", "p1", "a1", "speaker", 0),
                TestContext.CancellationToken));

        await using var cmdA = conn.CreateCommand();
        cmdA.CommandText = """
            SELECT status FROM meeting_invocations WHERE invocation_id = 'inv-a';
            """;
        Assert.AreEqual(
            "Running",
            await cmdA.ExecuteScalarAsync(TestContext.CancellationToken) as string);

        await using var cmdB = conn.CreateCommand();
        cmdB.CommandText = """
            SELECT session_id FROM meeting_invocations
            WHERE invocation_id = 'inv-conflict';
            """;
        Assert.AreEqual(
            sessionB,
            await cmdB.ExecuteScalarAsync(TestContext.CancellationToken) as string);
    }

    [TestMethod]
    public async Task CreateMeetingSession_InitialSelectionVersion_PersistsSessionAndParticipants()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        var participants = new[]
        {
            new MeetingParticipantInput("p1", "a1", null, "Alice", 0),
            new MeetingParticipantInput("p2", "a2", null, "Bob", 1)
        };

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h",
            participants, 7, TestContext.CancellationToken);

        var snap = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snap);
        Assert.AreEqual(7, snap.Session.SelectionVersion);
        Assert.HasCount(2, snap.Participants);
        Assert.IsTrue(snap.Participants.All(p => p.JoinedSelectionVersion == 7));
    }

    [TestMethod]
    public async Task CompleteRoundAndMeeting_RunningRound_CompletesBoth()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h",
            new[] { new MeetingParticipantInput("p1", "a1", null, "Alice", 0) },
            TestContext.CancellationToken);

        await repo.CreateRoundWithFirstInvocationAsync(
            sessionId, "run-1", 0, 1,
            new("inv-1", "p1", "a1", "speaker", 0),
            TestContext.CancellationToken);

        await repo.CompleteRoundAndMeetingAsync(
            sessionId, 1, "Completed", TestContext.CancellationToken);

        var snap = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snap);
        Assert.AreEqual("Completed", snap.Session.Status);
        Assert.IsNotNull(snap.CurrentRound);
        Assert.AreEqual("Completed", snap.CurrentRound.Status);
    }

    [TestMethod]
    public async Task CompleteRoundAndMeeting_SessionRoundConflict_RollsBackRoundUpdate()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h",
            new[] { new MeetingParticipantInput("p1", "a1", null, "Alice", 0) },
            TestContext.CancellationToken);

        await repo.CreateRoundWithFirstInvocationAsync(
            sessionId, "run-1", 0, 1,
            new("inv-1", "p1", "a1", "speaker", 0),
            TestContext.CancellationToken);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE meeting_sessions
                SET current_round = 2
                WHERE session_id = $sessionId;
                """;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            await cmd.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await repo.CompleteRoundAndMeetingAsync(
                sessionId, 1, "Completed", TestContext.CancellationToken));

        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = """
            SELECT status FROM meeting_rounds
            WHERE session_id = $sessionId AND round_index = 1;
            """;
        checkCmd.Parameters.AddWithValue("$sessionId", sessionId);
        var status = await checkCmd.ExecuteScalarAsync(TestContext.CancellationToken) as string;
        Assert.AreEqual("Running", status);
    }

    [TestMethod]
    public async Task CompleteInvocationAndStartNextRound_RunningInvocation_AdvancesAtomically()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h",
            new[]
            {
                new MeetingParticipantInput("p1", "a1", null, "Alice", 0),
                new MeetingParticipantInput("p2", "a2", null, "Bob", 1)
            },
            TestContext.CancellationToken);

        await repo.CreateRoundWithFirstInvocationAsync(
            sessionId, "run-1", 0, 1,
            new("inv-1", "p1", "a1", "speaker", 0),
            TestContext.CancellationToken);

        await repo.TransitionInvocationStatusAsync(
            "inv-1", "Scheduled", "Running", TestContext.CancellationToken);

        var result = await repo.CompleteInvocationAndStartNextRoundAsync(
            "inv-1", "Completed", "msg-1", null, null, 2,
            new MeetingInvocationScheduleInput("inv-2", "p2", "a2", "participant", 0),
            TestContext.CancellationToken);

        Assert.AreEqual("inv-2", result.InvocationId);
        Assert.AreEqual(2, result.RoundIndex);
        Assert.AreEqual(0, result.Ordinal);
        Assert.AreEqual("Scheduled", result.Status);

        var snap = await repo.GetMeetingSnapshotAsync(
            sessionId, TestContext.CancellationToken);
        Assert.IsNotNull(snap);
        Assert.AreEqual(2, snap.Session.CurrentRound);
        Assert.AreEqual("Running", snap.Session.Status);
        Assert.IsNotNull(snap.CurrentRound);
        Assert.AreEqual(2, snap.CurrentRound.RoundIndex);
        Assert.AreEqual("Running", snap.CurrentRound.Status);
        Assert.AreEqual("inv-2", snap.CurrentRound.FirstInvocationId);
        Assert.IsNotNull(snap.NextScheduledInvocation);
        Assert.AreEqual("inv-2", snap.NextScheduledInvocation.InvocationId);

        await using var cmdInv = conn.CreateCommand();
        cmdInv.CommandText = """
            SELECT status FROM meeting_invocations WHERE invocation_id = $invocationId;
            """;
        cmdInv.Parameters.AddWithValue("$invocationId", "inv-1");
        Assert.AreEqual(
            "Completed",
            await cmdInv.ExecuteScalarAsync(TestContext.CancellationToken) as string);

        await using var cmdRnd = conn.CreateCommand();
        cmdRnd.CommandText = """
            SELECT status FROM meeting_rounds
            WHERE session_id = $sessionId AND round_index = 1;
            """;
        cmdRnd.Parameters.AddWithValue("$sessionId", sessionId);
        Assert.AreEqual(
            "Completed",
            await cmdRnd.ExecuteScalarAsync(TestContext.CancellationToken) as string);
    }

    [TestMethod]
    public async Task CompleteInvocationAndStartNextRound_SessionRoundConflict_RollsBackAllChanges()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h",
            new[]
            {
                new MeetingParticipantInput("p1", "a1", null, "Alice", 0),
                new MeetingParticipantInput("p2", "a2", null, "Bob", 1)
            },
            TestContext.CancellationToken);

        await repo.CreateRoundWithFirstInvocationAsync(
            sessionId, "run-1", 0, 1,
            new("inv-1", "p1", "a1", "speaker", 0),
            TestContext.CancellationToken);

        await repo.TransitionInvocationStatusAsync(
            "inv-1", "Scheduled", "Running", TestContext.CancellationToken);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE meeting_sessions
                SET current_round = 2
                WHERE session_id = $sessionId;
                """;
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            await cmd.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await repo.CompleteInvocationAndStartNextRoundAsync(
                "inv-1", "Completed", null, null, null, 2,
                new MeetingInvocationScheduleInput("inv-2", "p2", "a2", "participant", 0),
                TestContext.CancellationToken));

        await using var cmdInv = conn.CreateCommand();
        cmdInv.CommandText = """
            SELECT status FROM meeting_invocations WHERE invocation_id = $invocationId;
            """;
        cmdInv.Parameters.AddWithValue("$invocationId", "inv-1");
        Assert.AreEqual(
            "Running",
            await cmdInv.ExecuteScalarAsync(TestContext.CancellationToken) as string);

        await using var cmdRnd = conn.CreateCommand();
        cmdRnd.CommandText = """
            SELECT status FROM meeting_rounds
            WHERE session_id = $sessionId AND round_index = 1;
            """;
        cmdRnd.Parameters.AddWithValue("$sessionId", sessionId);
        Assert.AreEqual(
            "Running",
            await cmdRnd.ExecuteScalarAsync(TestContext.CancellationToken) as string);

        await using var cmdRnd2 = conn.CreateCommand();
        cmdRnd2.CommandText = """
            SELECT COUNT(*) FROM meeting_rounds
            WHERE session_id = $sessionId AND round_index = 2;
            """;
        cmdRnd2.Parameters.AddWithValue("$sessionId", sessionId);
        Assert.AreEqual(
            0L,
            await cmdRnd2.ExecuteScalarAsync(TestContext.CancellationToken));

        await using var cmdInv2 = conn.CreateCommand();
        cmdInv2.CommandText = """
            SELECT COUNT(*) FROM meeting_invocations WHERE invocation_id = $invocationId;
            """;
        cmdInv2.Parameters.AddWithValue("$invocationId", "inv-2");
        Assert.AreEqual(
            0L,
            await cmdInv2.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task CompleteInvocationAndStartNextRound_SessionRunConflict_RollsBackAllChanges()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var repo = new SqliteMeetingRepository(conn);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);

        await repo.CreateMeetingSessionAsync(
            sessionId, "run-1", "{}", "h",
            new[]
            {
                new MeetingParticipantInput("p1", "a1", null, "Alice", 0),
                new MeetingParticipantInput("p2", "a2", null, "Bob", 1)
            },
            TestContext.CancellationToken);

        await repo.CreateRoundWithFirstInvocationAsync(
            sessionId, "run-1", 0, 1,
            new("inv-1", "p1", "a1", "speaker", 0),
            TestContext.CancellationToken);

        await repo.TransitionInvocationStatusAsync(
            "inv-1", "Scheduled", "Running", TestContext.CancellationToken);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE meeting_sessions
                SET run_id = $runId
                WHERE session_id = $sessionId;
                """;
            cmd.Parameters.AddWithValue("$runId", "external-run");
            cmd.Parameters.AddWithValue("$sessionId", sessionId);
            await cmd.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await repo.CompleteInvocationAndStartNextRoundAsync(
                "inv-1", "Completed", null, null, null, 2,
                new MeetingInvocationScheduleInput("inv-2", "p2", "a2", "participant", 0),
                TestContext.CancellationToken));

        await using var cmdInv = conn.CreateCommand();
        cmdInv.CommandText = """
            SELECT status FROM meeting_invocations WHERE invocation_id = $invocationId;
            """;
        cmdInv.Parameters.AddWithValue("$invocationId", "inv-1");
        Assert.AreEqual(
            "Running",
            await cmdInv.ExecuteScalarAsync(TestContext.CancellationToken) as string);

        await using var cmdRnd = conn.CreateCommand();
        cmdRnd.CommandText = """
            SELECT status FROM meeting_rounds
            WHERE session_id = $sessionId AND round_index = 1;
            """;
        cmdRnd.Parameters.AddWithValue("$sessionId", sessionId);
        Assert.AreEqual(
            "Running",
            await cmdRnd.ExecuteScalarAsync(TestContext.CancellationToken) as string);

        await using var cmdRun = conn.CreateCommand();
        cmdRun.CommandText = """
            SELECT run_id FROM meeting_sessions
            WHERE session_id = $sessionId;
            """;
        cmdRun.Parameters.AddWithValue("$sessionId", sessionId);
        Assert.AreEqual(
            "external-run",
            await cmdRun.ExecuteScalarAsync(TestContext.CancellationToken) as string);

        await using var cmdRnd2 = conn.CreateCommand();
        cmdRnd2.CommandText = """
            SELECT COUNT(*) FROM meeting_rounds
            WHERE session_id = $sessionId AND round_index = 2;
            """;
        cmdRnd2.Parameters.AddWithValue("$sessionId", sessionId);
        Assert.AreEqual(
            0L,
            await cmdRnd2.ExecuteScalarAsync(TestContext.CancellationToken));

        await using var cmdInv2 = conn.CreateCommand();
        cmdInv2.CommandText = """
            SELECT COUNT(*) FROM meeting_invocations WHERE invocation_id = $invocationId;
            """;
        cmdInv2.Parameters.AddWithValue("$invocationId", "inv-2");
        Assert.AreEqual(
            0L,
            await cmdInv2.ExecuteScalarAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task DataDirectoryInitializer_Restart_InterruptsRunningMeetingInvocation()
    {
        var dataDirectory = Path.Combine(
            TestContext.TestRunDirectory ?? Path.GetTempPath(),
            $"meeting-recovery-{Guid.NewGuid():N}");

        try
        {
            string sessionId;
            await using (var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken))
            {
                sessionId = await SeedSessionAsync(connection, TestContext.CancellationToken);
                var repository = new SqliteMeetingRepository(connection);
                await repository.CreateMeetingSessionAsync(
                    sessionId,
                    "run-1",
                    "{}",
                    "h",
                    new[]
                    {
                        new MeetingParticipantInput("p1", "a1", null, "Alice", 0),
                        new MeetingParticipantInput("p2", "a2", null, "Bob", 1)
                    },
                    TestContext.CancellationToken);
                await repository.CreateRoundWithFirstInvocationAsync(
                    sessionId,
                    "run-1",
                    expectedCurrentRound: 0,
                    newRoundIndex: 1,
                    firstInvocation: new MeetingInvocationScheduleInput(
                        "inv-running", "p1", "a1", "participant", 0),
                    ct: TestContext.CancellationToken);
                await repository.TransitionInvocationStatusAsync(
                    "inv-running",
                    "Scheduled",
                    "Running",
                    TestContext.CancellationToken);
            }

            await using var recoveredConnection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var recoveredRepository = new SqliteMeetingRepository(recoveredConnection);
            var recoverable = await recoveredRepository.QueryRecoverableInvocationsAsync(
                sessionId,
                TestContext.CancellationToken);

            Assert.HasCount(1, recoverable);
            Assert.AreEqual("inv-running", recoverable[0].InvocationId);
            Assert.AreEqual("Interrupted", recoverable[0].Status);
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

    [TestMethod]
    public async Task ResumeRecoverableInvocation_Interrupted_ResumesOriginalRunAndConsumesOneRetry()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);
        var sessionRepository = new SqliteSessionRepository(conn);
        var runId = await CreateRunningRunAsync(
            sessionRepository,
            sessionId,
            "resume-interrupted",
            TestContext.CancellationToken);
        var repository = new SqliteMeetingRepository(conn);
        await repository.CreateMeetingSessionAsync(
            sessionId,
            runId,
            "{}",
            "h",
            [
                new MeetingParticipantInput("p1", "a1", null, "Alice", 0),
                new MeetingParticipantInput("p2", "a2", null, "Bob", 1)
            ],
            TestContext.CancellationToken);
        await repository.CreateRoundWithFirstInvocationAsync(
            sessionId,
            runId,
            0,
            1,
            new MeetingInvocationScheduleInput("inv-resume", "p1", "a1", "participant", 0),
            TestContext.CancellationToken);
        await repository.TransitionInvocationStatusAsync(
            "inv-resume",
            "Scheduled",
            "Running",
            TestContext.CancellationToken);
        await sessionRepository.MarkInterruptedAsync(TestContext.CancellationToken);
        await repository.RecoverRunningInvocationsAsync(TestContext.CancellationToken);

        var resumed = await repository.ResumeRecoverableInvocationAsync(
            "inv-resume",
            maxRetryCount: 1,
            ct: TestContext.CancellationToken);
        var replayed = await repository.ResumeRecoverableInvocationAsync(
            "inv-resume",
            maxRetryCount: 1,
            ct: TestContext.CancellationToken);

        Assert.AreEqual("Running", resumed.Status);
        Assert.AreEqual(1, resumed.RetryCount);
        Assert.IsNull(resumed.CompletedAt);
        Assert.AreEqual(resumed, replayed);
        Assert.AreEqual(
            RunStatus.Running,
            await sessionRepository.GetRunStatusAsync(runId, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ResumeRecoverableInvocation_WithCanonicalMessage_BypassesRetryLimitWithoutIncrement()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);
        var sessionRepository = new SqliteSessionRepository(conn);
        var runId = await CreateRunningRunAsync(
            sessionRepository,
            sessionId,
            "resume-canonical",
            TestContext.CancellationToken);
        var repository = new SqliteMeetingRepository(conn);
        await repository.CreateMeetingSessionAsync(
            sessionId,
            runId,
            "{}",
            "h",
            [
                new MeetingParticipantInput("p1", "a1", null, "Alice", 0),
                new MeetingParticipantInput("p2", "a2", null, "Bob", 1)
            ],
            TestContext.CancellationToken);
        await repository.CreateRoundWithFirstInvocationAsync(
            sessionId,
            runId,
            0,
            1,
            new MeetingInvocationScheduleInput(
                "inv-canonical",
                "p1",
                "a1",
                "participant",
                0),
            TestContext.CancellationToken);
        await repository.TransitionInvocationStatusAsync(
            "inv-canonical",
            "Scheduled",
            "Running",
            TestContext.CancellationToken);
        await sessionRepository.MarkInterruptedAsync(TestContext.CancellationToken);
        await repository.RecoverRunningInvocationsAsync(TestContext.CancellationToken);

        var resumed = await repository.ResumeRecoverableInvocationAsync(
            "inv-canonical",
            maxRetryCount: 0,
            resumeWithoutProvider: true,
            ct: TestContext.CancellationToken);

        Assert.AreEqual("Running", resumed.Status);
        Assert.AreEqual(0, resumed.RetryCount);
        Assert.AreEqual(
            RunStatus.Running,
            await sessionRepository.GetRunStatusAsync(runId, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ResumeRecoverableInvocation_Scheduled_DoesNotConsumeRetry()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);
        var sessionRepository = new SqliteSessionRepository(conn);
        var runId = await CreateRunningRunAsync(
            sessionRepository,
            sessionId,
            "resume-scheduled",
            TestContext.CancellationToken);
        var repository = new SqliteMeetingRepository(conn);
        await repository.CreateMeetingSessionAsync(
            sessionId,
            runId,
            "{}",
            "h",
            [
                new MeetingParticipantInput("p1", "a1", null, "Alice", 0),
                new MeetingParticipantInput("p2", "a2", null, "Bob", 1)
            ],
            TestContext.CancellationToken);
        await repository.CreateRoundWithFirstInvocationAsync(
            sessionId,
            runId,
            0,
            1,
            new MeetingInvocationScheduleInput("inv-scheduled", "p1", "a1", "participant", 0),
            TestContext.CancellationToken);
        await sessionRepository.MarkInterruptedAsync(TestContext.CancellationToken);

        var resumed = await repository.ResumeRecoverableInvocationAsync(
            "inv-scheduled",
            maxRetryCount: 0,
            ct: TestContext.CancellationToken);

        Assert.AreEqual("Running", resumed.Status);
        Assert.AreEqual(0, resumed.RetryCount);
        Assert.IsNotNull(resumed.StartedAt);
        Assert.AreEqual(
            RunStatus.Running,
            await sessionRepository.GetRunStatusAsync(runId, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ResumeRecoverableInvocation_MeetingConflict_RollsBackRunAndInvocation()
    {
        await using var conn = await CreateFullDatabaseAsync(TestContext.CancellationToken);
        var sessionId = await SeedSessionAsync(conn, TestContext.CancellationToken);
        var sessionRepository = new SqliteSessionRepository(conn);
        var runId = await CreateRunningRunAsync(
            sessionRepository,
            sessionId,
            "resume-conflict",
            TestContext.CancellationToken);
        var repository = new SqliteMeetingRepository(conn);
        await repository.CreateMeetingSessionAsync(
            sessionId,
            runId,
            "{}",
            "h",
            [
                new MeetingParticipantInput("p1", "a1", null, "Alice", 0),
                new MeetingParticipantInput("p2", "a2", null, "Bob", 1)
            ],
            TestContext.CancellationToken);
        await repository.CreateRoundWithFirstInvocationAsync(
            sessionId,
            runId,
            0,
            1,
            new MeetingInvocationScheduleInput("inv-conflict", "p1", "a1", "participant", 0),
            TestContext.CancellationToken);
        await repository.TransitionInvocationStatusAsync(
            "inv-conflict",
            "Scheduled",
            "Running",
            TestContext.CancellationToken);
        await sessionRepository.MarkInterruptedAsync(TestContext.CancellationToken);
        await repository.RecoverRunningInvocationsAsync(TestContext.CancellationToken);
        await repository.TryTransitionMeetingStatusAsync(
            sessionId,
            "Running",
            "Failed",
            TestContext.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await repository.ResumeRecoverableInvocationAsync(
                "inv-conflict",
                maxRetryCount: 1,
                ct: TestContext.CancellationToken));

        Assert.AreEqual(
            RunStatus.Interrupted,
            await sessionRepository.GetRunStatusAsync(runId, TestContext.CancellationToken));
        var recoverable = await repository.QueryRecoverableInvocationsAsync(
            sessionId,
            TestContext.CancellationToken);
        Assert.HasCount(1, recoverable);
        Assert.AreEqual("Interrupted", recoverable[0].Status);
        Assert.AreEqual(0, recoverable[0].RetryCount);
    }

    [TestMethod]
    public async Task ParticipantCanonicalMessage_AfterRestart_ReconcilesWithoutJsonlReplay()
    {
        var dataDirectory = Path.Combine(
            TestContext.TestRunDirectory ?? Path.GetTempPath(),
            $"meeting-participant-reconcile-{Guid.NewGuid():N}");

        try
        {
            string sessionId;
            await using (var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken))
            {
                sessionId = await SeedSessionAsync(connection, TestContext.CancellationToken);
                var repository = new SqliteMeetingRepository(connection);
                await repository.CreateMeetingSessionAsync(
                    sessionId,
                    "run-1",
                    "{}",
                    "policy-hash",
                    [
                        new MeetingParticipantInput("p1", "a1", null, "Alice", 0),
                        new MeetingParticipantInput("p2", "a2", null, "Bob", 1)
                    ],
                    TestContext.CancellationToken);
                await repository.CreateRoundWithFirstInvocationAsync(
                    sessionId,
                    "run-1",
                    expectedCurrentRound: 0,
                    newRoundIndex: 1,
                    firstInvocation: new MeetingInvocationScheduleInput(
                        "inv-participant", "p1", "a1", "participant", 0),
                    ct: TestContext.CancellationToken);
                await repository.TransitionInvocationStatusAsync(
                    "inv-participant",
                    "Scheduled",
                    "Running",
                    TestContext.CancellationToken);

                var store = new ConversationStore(dataDirectory);
                await store.AppendMessageAsync(
                    sessionId,
                    "Meeting",
                    new ConversationRecordV1(
                        "message-participant",
                        1,
                        "inv-participant",
                        "a1",
                        "assistant",
                        CreateTextContent("durable participant response"),
                        DateTimeOffset.UtcNow),
                    TestContext.CancellationToken);
            }

            await using var recoveredConnection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var recoveredRepository = new SqliteMeetingRepository(recoveredConnection);
            var recoveredStore = new ConversationStore(dataDirectory);
            var canonical = await recoveredStore.FindMessagesByInvocationIdAsync(
                sessionId,
                "inv-participant",
                TestContext.CancellationToken);

            Assert.HasCount(1, canonical);
            Assert.AreEqual("message-participant", canonical[0].MessageId);
            var reconciled = await recoveredRepository.ReconcileInterruptedInvocationAsync(
                "inv-participant",
                canonical[0].MessageId,
                TestContext.CancellationToken);
            Assert.AreEqual("Completed", reconciled.Status);

            var history = await recoveredStore.ReadAllAsync(
                sessionId,
                TestContext.CancellationToken);
            Assert.HasCount(1, history);
            Assert.HasCount(
                2,
                await File.ReadAllLinesAsync(
                    Path.Combine(dataDirectory, "messages", $"{sessionId}.jsonl"),
                    TestContext.CancellationToken));
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

    [TestMethod]
    public async Task SummaryCanonicalMessage_AfterRestart_RestoresRoundSummaryAtomically()
    {
        var dataDirectory = Path.Combine(
            TestContext.TestRunDirectory ?? Path.GetTempPath(),
            $"meeting-summary-reconcile-{Guid.NewGuid():N}");

        try
        {
            string sessionId;
            await using (var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken))
            {
                sessionId = await SeedSessionAsync(connection, TestContext.CancellationToken);
                var repository = new SqliteMeetingRepository(connection);
                await repository.CreateMeetingSessionAsync(
                    sessionId,
                    "run-1",
                    "{}",
                    "policy-hash",
                    [
                        new MeetingParticipantInput("p1", "a1", null, "Alice", 0),
                        new MeetingParticipantInput("p2", "a2", null, "Bob", 1)
                    ],
                    TestContext.CancellationToken);
                await repository.CreateRoundWithFirstInvocationAsync(
                    sessionId,
                    "run-1",
                    expectedCurrentRound: 0,
                    newRoundIndex: 1,
                    firstInvocation: new MeetingInvocationScheduleInput(
                        "inv-summary", null, "meeting.summarizer", "summarizer", 0),
                    ct: TestContext.CancellationToken);
                await repository.TransitionInvocationStatusAsync(
                    "inv-summary",
                    "Scheduled",
                    "Running",
                    TestContext.CancellationToken);

                var store = new ConversationStore(dataDirectory);
                await store.AppendMessageAsync(
                    sessionId,
                    "Meeting",
                    new ConversationRecordV1(
                        "message-summary",
                        1,
                        "inv-summary",
                        "meeting.summarizer",
                        "assistant",
                        CreateTextContent("durable summary"),
                        DateTimeOffset.UtcNow,
                        SummaryMetadata: CreateSummaryMetadata()),
                    TestContext.CancellationToken);
            }

            await using var recoveredConnection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var recoveredRepository = new SqliteMeetingRepository(recoveredConnection);
            var recoveredStore = new ConversationStore(dataDirectory);
            var canonical = await recoveredStore.FindMessagesByInvocationIdAsync(
                sessionId,
                "inv-summary",
                TestContext.CancellationToken);

            Assert.HasCount(1, canonical);
            Assert.IsNotNull(canonical[0].SummaryMetadata);
            await recoveredRepository.ReconcileInterruptedSummaryAsync(
                "inv-summary",
                canonical[0].MessageId,
                summarizesThroughSeq: 0,
                summaryPolicyHash: "policy-hash",
                summaryAgentId: "meeting.summarizer",
                TestContext.CancellationToken);

            var snapshot = await recoveredRepository.GetMeetingSnapshotAsync(
                sessionId,
                TestContext.CancellationToken);
            Assert.IsNotNull(snapshot);
            Assert.IsNotNull(snapshot.LatestSummary);
            Assert.AreEqual("message-summary", snapshot.LatestSummary.SummaryMessageId);
            Assert.AreEqual("inv-summary", snapshot.LatestSummary.SummaryInvocationId);
            Assert.IsNull(snapshot.NextScheduledInvocation);
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

    private static async Task<string> CreateRunningRunAsync(
        SqliteSessionRepository repository,
        string sessionId,
        string idempotencyKey,
        CancellationToken ct)
    {
        var runId = await repository.CreateRunAsync(
            sessionId,
            idempotencyKey,
            TimeSpan.FromHours(1),
            requestHash: null,
            ct);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Accepted,
            RunStatus.Preparing,
            ct);
        await repository.TransitionRunStatusAsync(
            runId,
            RunStatus.Preparing,
            RunStatus.Running,
            ct);
        return runId;
    }

    private static JsonElement CreateTextContent(string text)
    {
        var encoded = JsonEncodedText.Encode(text).ToString();
        using var document = JsonDocument.Parse(
            $"[{{\"type\":\"text\",\"text\":\"{encoded}\"}}]");
        return document.RootElement.Clone();
    }

    private static JsonElement CreateSummaryMetadata()
    {
        using var document = JsonDocument.Parse("""
            {
              "roundIndex": 1,
              "summarizesThroughSeq": 0,
              "policyHash": "policy-hash",
              "summarizerAgentId": "meeting.summarizer",
              "summarizerInvocationId": "inv-summary"
            }
            """);
        return document.RootElement.Clone();
    }

    #endregion
}
