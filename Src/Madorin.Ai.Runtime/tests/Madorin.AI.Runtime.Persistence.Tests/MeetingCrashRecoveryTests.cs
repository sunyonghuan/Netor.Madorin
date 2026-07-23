using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Tests;

[TestClass]
public sealed class MeetingCrashRecoveryTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RoundCreation_CommittedThenCrash_RestartKeepsSingleScheduledInvocation()
    {
        var dataDirectory = CreateDataDirectory("round");

        try
        {
            string sessionId;
            await using (var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken))
            {
                sessionId = await SeedSessionAsync(connection, TestContext.CancellationToken);
                await CreateMeetingAsync(connection, sessionId, TestContext.CancellationToken);
                var repository = new SqliteMeetingRepository(
                    connection,
                    CrashAfter(SqliteMeetingRepositoryFailurePoint.AfterRoundCreationCommit));

                await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                    async () => await repository.CreateRoundWithFirstInvocationAsync(
                        sessionId,
                        "run-1",
                        expectedCurrentRound: 0,
                        newRoundIndex: 1,
                        new MeetingInvocationScheduleInput(
                            "inv-round", "p1", "a1", "participant", 0),
                        TestContext.CancellationToken));
            }

            await using var recoveredConnection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var recoveredRepository = new SqliteMeetingRepository(recoveredConnection);
            var snapshot = await recoveredRepository.GetMeetingSnapshotAsync(
                sessionId,
                TestContext.CancellationToken);

            Assert.IsNotNull(snapshot);
            Assert.AreEqual(1, snapshot.Session.CurrentRound);
            Assert.IsNotNull(snapshot.CurrentRound);
            Assert.AreEqual("Running", snapshot.CurrentRound.Status);
            Assert.AreEqual("inv-round", snapshot.CurrentRound.FirstInvocationId);
            Assert.IsNotNull(snapshot.NextScheduledInvocation);
            Assert.AreEqual("inv-round", snapshot.NextScheduledInvocation.InvocationId);
            Assert.AreEqual("Scheduled", snapshot.NextScheduledInvocation.Status);
            Assert.AreEqual(
                1L,
                await CountAsync(
                    recoveredConnection,
                    "SELECT COUNT(*) FROM meeting_rounds WHERE session_id = $id;",
                    sessionId,
                    TestContext.CancellationToken));
            Assert.AreEqual(
                1L,
                await CountAsync(
                    recoveredConnection,
                    "SELECT COUNT(*) FROM meeting_invocations WHERE session_id = $id;",
                    sessionId,
                    TestContext.CancellationToken));

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await recoveredRepository.CreateRoundWithFirstInvocationAsync(
                    sessionId,
                    "run-1",
                    expectedCurrentRound: 0,
                    newRoundIndex: 1,
                    new MeetingInvocationScheduleInput(
                        "inv-round-replay", "p1", "a1", "participant", 0),
                    TestContext.CancellationToken));
        }
        finally
        {
            Cleanup(dataDirectory);
        }
    }

    [TestMethod]
    public async Task ParticipantCompletion_CommittedThenCrash_RestartDoesNotReplayDurableOutput()
    {
        var dataDirectory = CreateDataDirectory("participant");

        try
        {
            string sessionId;
            await using (var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken))
            {
                sessionId = await SeedRunningInvocationAsync(
                    connection,
                    new MeetingInvocationScheduleInput(
                        "inv-participant", "p1", "a1", "participant", 0),
                    TestContext.CancellationToken);
                var store = new ConversationStore(dataDirectory);
                await store.AppendMessageAsync(
                    sessionId,
                    "Meeting",
                    CreateRecord(
                        "message-participant",
                        "inv-participant",
                        "a1",
                        "durable participant response"),
                    TestContext.CancellationToken);

                var repository = new SqliteMeetingRepository(
                    connection,
                    CrashAfter(SqliteMeetingRepositoryFailurePoint.AfterInvocationCompletionCommit));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                    async () => await repository.CompleteInvocationAsync(
                        "inv-participant",
                        "Completed",
                        "message-participant",
                        selectorDecisionJson: null,
                        errorCode: null,
                        errorMessage: null,
                        new MeetingInvocationScheduleInput(
                            "inv-next", "p2", "a2", "participant", 0),
                        TestContext.CancellationToken));
            }

            await using var recoveredConnection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var recoveredRepository = new SqliteMeetingRepository(recoveredConnection);
            var recoveredStore = new ConversationStore(dataDirectory);
            var invocation = await ReadInvocationAsync(
                recoveredConnection,
                "inv-participant",
                TestContext.CancellationToken);
            var canonical = await recoveredStore.FindMessagesByInvocationIdAsync(
                sessionId,
                "inv-participant",
                TestContext.CancellationToken);
            var snapshot = await recoveredRepository.GetMeetingSnapshotAsync(
                sessionId,
                TestContext.CancellationToken);

            Assert.AreEqual("Completed", invocation.Status);
            Assert.AreEqual("message-participant", invocation.MessageId);
            Assert.IsNull(invocation.SelectorDecisionJson);
            Assert.HasCount(1, canonical);
            Assert.AreEqual("message-participant", canonical[0].MessageId);
            Assert.IsNotNull(snapshot);
            Assert.IsNotNull(snapshot.NextScheduledInvocation);
            Assert.AreEqual("inv-next", snapshot.NextScheduledInvocation.InvocationId);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await recoveredRepository.CompleteInvocationAsync(
                    "inv-participant",
                    "Completed",
                    "message-participant-replay",
                    selectorDecisionJson: null,
                    errorCode: null,
                    errorMessage: null,
                    nextInvocation: null,
                    TestContext.CancellationToken));
        }
        finally
        {
            Cleanup(dataDirectory);
        }
    }

    [TestMethod]
    public async Task SelectorDecision_CommittedThenCrash_RestartKeepsDecisionAndNextSpeaker()
    {
        var dataDirectory = CreateDataDirectory("selector");
        const string decisionJson = "{\"participantId\":\"p2\",\"reason\":\"next\"}";

        try
        {
            string sessionId;
            await using (var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken))
            {
                sessionId = await SeedRunningInvocationAsync(
                    connection,
                    new MeetingInvocationScheduleInput(
                        "inv-selector", null, "meeting.selector", "selector", 0),
                    TestContext.CancellationToken);
                var repository = new SqliteMeetingRepository(
                    connection,
                    CrashAfter(SqliteMeetingRepositoryFailurePoint.AfterSelectorDecisionCommit));

                await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                    async () => await repository.CompleteInvocationAsync(
                        "inv-selector",
                        "Completed",
                        messageId: null,
                        decisionJson,
                        errorCode: null,
                        errorMessage: null,
                        new MeetingInvocationScheduleInput(
                            "inv-selected", "p2", "a2", "participant", 0),
                        TestContext.CancellationToken));
            }

            await using var recoveredConnection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var recoveredRepository = new SqliteMeetingRepository(recoveredConnection);
            var selector = await ReadInvocationAsync(
                recoveredConnection,
                "inv-selector",
                TestContext.CancellationToken);
            var snapshot = await recoveredRepository.GetMeetingSnapshotAsync(
                sessionId,
                TestContext.CancellationToken);

            Assert.AreEqual("Completed", selector.Status);
            Assert.IsNull(selector.MessageId);
            Assert.AreEqual(decisionJson, selector.SelectorDecisionJson);
            Assert.IsNotNull(snapshot);
            Assert.IsNotNull(snapshot.NextScheduledInvocation);
            Assert.AreEqual("inv-selected", snapshot.NextScheduledInvocation.InvocationId);
            Assert.AreEqual("p2", snapshot.NextScheduledInvocation.ParticipantId);
            Assert.AreEqual(
                1L,
                await CountAsync(
                    recoveredConnection,
                    "SELECT COUNT(*) FROM meeting_invocations WHERE invocation_id = $id;",
                    "inv-selected",
                    TestContext.CancellationToken));

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await recoveredRepository.CompleteInvocationAsync(
                    "inv-selector",
                    "Completed",
                    messageId: null,
                    decisionJson,
                    errorCode: null,
                    errorMessage: null,
                    nextInvocation: null,
                    TestContext.CancellationToken));
        }
        finally
        {
            Cleanup(dataDirectory);
        }
    }

    [TestMethod]
    public async Task SummaryWrite_FlushedThenCrash_RestartReconcilesWithoutDuplicateSummary()
    {
        var dataDirectory = CreateDataDirectory("summary");
        var summaryRecord = CreateRecord(
            "message-summary",
            "inv-summary",
            "meeting.summarizer",
            "durable summary",
            CreateSummaryMetadata());

        try
        {
            string sessionId;
            await using (var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken))
            {
                sessionId = await SeedRunningInvocationAsync(
                    connection,
                    new MeetingInvocationScheduleInput(
                        "inv-summary", null, "meeting.summarizer", "summarizer", 0),
                    TestContext.CancellationToken);
                var store = new ConversationStore(
                    dataDirectory,
                    new ConversationStoreOptions
                    {
                        FailureInjector = (point, _) =>
                            point == ConversationStoreFailurePoint.AfterFileFlush
                                ? ValueTask.FromException(
                                    new InvalidOperationException($"Injected crash at {point}."))
                                : ValueTask.CompletedTask
                    });

                await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                    async () => await store.AppendMessageAsync(
                        sessionId,
                        "Meeting",
                        summaryRecord,
                        TestContext.CancellationToken));
            }

            await using var recoveredConnection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var recoveredRepository = new SqliteMeetingRepository(recoveredConnection);
            var recoveredStore = new ConversationStore(dataDirectory);
            await recoveredStore.RepairIfNeededAsync(
                sessionId,
                TestContext.CancellationToken);
            var canonical = await recoveredStore.FindMessagesByInvocationIdAsync(
                sessionId,
                "inv-summary",
                TestContext.CancellationToken);

            Assert.HasCount(1, canonical);
            Assert.AreEqual("message-summary", canonical[0].MessageId);
            Assert.IsNotNull(canonical[0].SummaryMetadata);
            await recoveredRepository.ReconcileInterruptedSummaryAsync(
                "inv-summary",
                canonical[0].MessageId,
                summarizesThroughSeq: 0,
                summaryPolicyHash: "policy-hash",
                summaryAgentId: "meeting.summarizer",
                TestContext.CancellationToken);

            await recoveredStore.AppendMessageAsync(
                sessionId,
                "Meeting",
                summaryRecord,
                TestContext.CancellationToken);
            var history = await recoveredStore.ReadAllAsync(
                sessionId,
                TestContext.CancellationToken);
            var snapshot = await recoveredRepository.GetMeetingSnapshotAsync(
                sessionId,
                TestContext.CancellationToken);
            var invocation = await ReadInvocationAsync(
                recoveredConnection,
                "inv-summary",
                TestContext.CancellationToken);

            Assert.HasCount(1, history);
            Assert.AreEqual("Completed", invocation.Status);
            Assert.AreEqual("message-summary", invocation.MessageId);
            Assert.IsNotNull(snapshot);
            Assert.IsNotNull(snapshot.LatestSummary);
            Assert.AreEqual("message-summary", snapshot.LatestSummary.SummaryMessageId);
            Assert.AreEqual("inv-summary", snapshot.LatestSummary.SummaryInvocationId);
            Assert.IsNull(snapshot.NextScheduledInvocation);
        }
        finally
        {
            Cleanup(dataDirectory);
        }
    }

    private static string CreateDataDirectory(string boundary) =>
        Path.Combine(
            Path.GetTempPath(),
            "madorin-meeting-crash-tests",
            $"{boundary}-{Guid.NewGuid():N}");

    private static SqliteMeetingRepositoryOptions CrashAfter(
        SqliteMeetingRepositoryFailurePoint failurePoint) =>
        new()
        {
            FailureInjector = (actual, _) => actual == failurePoint
                ? ValueTask.FromException(
                    new InvalidOperationException($"Injected crash at {actual}."))
                : ValueTask.CompletedTask
        };

    private static async Task CreateMeetingAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken ct)
    {
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
            ct);
    }

    private static async Task<string> SeedRunningInvocationAsync(
        SqliteConnection connection,
        MeetingInvocationScheduleInput invocation,
        CancellationToken ct)
    {
        var sessionId = await SeedSessionAsync(connection, ct);
        await CreateMeetingAsync(connection, sessionId, ct);
        var repository = new SqliteMeetingRepository(connection);
        await repository.CreateRoundWithFirstInvocationAsync(
            sessionId,
            "run-1",
            expectedCurrentRound: 0,
            newRoundIndex: 1,
            invocation,
            ct);
        await repository.TransitionInvocationStatusAsync(
            invocation.InvocationId,
            "Scheduled",
            "Running",
            ct);
        return sessionId;
    }

    private static async Task<string> SeedSessionAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions(session_id, mode, status, created_at, updated_at)
            VALUES($sessionId, 'Meeting', 'Active', $now, $now);
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue(
            "$now",
            DateTimeOffset.UtcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct);
        return sessionId;
    }

    private static ConversationRecordV1 CreateRecord(
        string messageId,
        string invocationId,
        string agentId,
        string text,
        JsonElement? summaryMetadata = null) =>
        new(
            messageId,
            1,
            invocationId,
            agentId,
            "assistant",
            CreateTextContent(text),
            DateTimeOffset.UtcNow,
            SummaryMetadata: summaryMetadata);

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

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        string sql,
        string id,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<InvocationState> ReadInvocationAsync(
        SqliteConnection connection,
        string invocationId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, message_id, selector_decision_json
            FROM meeting_invocations
            WHERE invocation_id = $invocationId;
            """;
        command.Parameters.AddWithValue("$invocationId", invocationId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        Assert.IsTrue(await reader.ReadAsync(ct));
        return new InvocationState(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static void Cleanup(string dataDirectory)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(dataDirectory))
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private sealed record InvocationState(
        string Status,
        string? MessageId,
        string? SelectorDecisionJson);
}
