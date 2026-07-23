using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Modes.Meeting;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MeetingPerformanceBaselineTests
{
    private const int ParticipantCount = 20;
    private const int LongMeetingRoundCount = 50;
    private const int SampleCount = 31;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Performance")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Projection_20Participants50Rounds_RecordsP50P95AndAllocations()
    {
        var shortMeeting = CreateProjectionFixture(roundCount: 10);
        var longMeeting = CreateProjectionFixture(LongMeetingRoundCount);

        await WarmProjectionAsync(shortMeeting.Request, TestContext.CancellationToken);
        await WarmProjectionAsync(longMeeting.Request, TestContext.CancellationToken);

        var shortBaseline = await MeasureProjectionAsync(
            shortMeeting.Request,
            TestContext.CancellationToken);
        var longBaseline = await MeasureProjectionAsync(
            longMeeting.Request,
            TestContext.CancellationToken);

        TestContext.WriteLine(
            "Meeting projection baseline: participants={0}; strategy=SummaryCompressed; "
            + "10 rounds p50={1:F3} ms, p95={2:F3} ms, allocated={3:N0} B/op; "
            + "50 rounds p50={4:F3} ms, p95={5:F3} ms, allocated={6:N0} B/op.",
            ParticipantCount,
            shortBaseline.P50Milliseconds,
            shortBaseline.P95Milliseconds,
            shortBaseline.AllocatedBytesPerOperation,
            longBaseline.P50Milliseconds,
            longBaseline.P95Milliseconds,
            longBaseline.AllocatedBytesPerOperation);

        Assert.AreEqual(LongMeetingRoundCount, longBaseline.Result.TotalRoundCount);
        Assert.IsGreaterThan(0, longBaseline.Result.DroppedRoundCount);
        Assert.IsGreaterThanOrEqualTo(
            2,
            longBaseline.Result.Messages.Length,
            "The current round must remain present in the compressed projection.");
        Assert.IsLessThan(2_000, longBaseline.P95Milliseconds);
        Assert.IsLessThan(64L * 1024 * 1024, longBaseline.AllocatedBytesPerOperation);
    }

    private static ProjectionFixture CreateProjectionFixture(int roundCount)
    {
        var records = new List<ConversationRecordV1>(roundCount * 2);
        var invocationToRound = new Dictionary<string, int>(roundCount * 2, StringComparer.Ordinal);
        var invocationToRole = new Dictionary<string, MeetingInvocationRole>(
            roundCount * 2,
            StringComparer.Ordinal);
        long sequence = 0;
        var payload = new string('x', 512);

        for (var roundIndex = 1; roundIndex <= roundCount; roundIndex++)
        {
            var participantInvocationId = $"participant-{roundIndex}";
            var participantSequence = ++sequence;
            records.Add(new ConversationRecordV1(
                $"message-{participantInvocationId}",
                participantSequence,
                participantInvocationId,
                $"participant-agent-{(roundIndex - 1) % ParticipantCount}",
                "assistant",
                SerializeContent($"round-{roundIndex}:{payload}"),
                DateTimeOffset.UnixEpoch.AddSeconds(participantSequence)));
            invocationToRound.Add(participantInvocationId, roundIndex);
            invocationToRole.Add(participantInvocationId, MeetingInvocationRole.Participant);

            var summaryInvocationId = $"summary-{roundIndex}";
            var summarySequence = ++sequence;
            records.Add(new ConversationRecordV1(
                $"message-{summaryInvocationId}",
                summarySequence,
                summaryInvocationId,
                "meeting.summarizer",
                "assistant",
                SerializeContent($"summary for round {roundIndex}"),
                DateTimeOffset.UnixEpoch.AddSeconds(summarySequence),
                SummaryMetadata: CreateSummaryMetadata(roundIndex, participantSequence)));
            invocationToRound.Add(summaryInvocationId, roundIndex);
            invocationToRole.Add(summaryInvocationId, MeetingInvocationRole.Summarizer);
        }

        return new ProjectionFixture(
            new MeetingProjectionRequest(
                records,
                invocationToRound,
                CurrentRound: roundCount,
                TokenLimit: 1_000_000,
                Strategy: MeetingContextStrategy.SummaryCompressed,
                InvocationToRole: invocationToRole,
                TargetRole: MeetingInvocationRole.Participant));
    }

    private static JsonElement SerializeContent(string text) =>
        JsonSerializer.SerializeToElement(
            new ContentBlock[] { new TextContentBlock(text) },
            RuntimeJsonContext.Default.ContentBlockArray);

    private static JsonElement CreateSummaryMetadata(int roundIndex, long summarizesThroughSeq)
    {
        using var document = JsonDocument.Parse(
            $$"""
              {
                "roundIndex": {{roundIndex}},
                "summarizesThroughSeq": {{summarizesThroughSeq}}
              }
              """);
        return document.RootElement.Clone();
    }

    private static async Task WarmProjectionAsync(
        MeetingProjectionRequest request,
        CancellationToken ct)
    {
        for (var index = 0; index < 3; index++)
        {
            _ = await MeetingContextProjectionService.BuildAsync(request, ct);
        }
    }

    private static async Task<ProjectionBaseline> MeasureProjectionAsync(
        MeetingProjectionRequest request,
        CancellationToken ct)
    {
        var elapsedMilliseconds = new double[SampleCount];
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        MeetingProjectionResult? result = null;

        for (var index = 0; index < SampleCount; index++)
        {
            var startedAt = Stopwatch.GetTimestamp();
            result = await MeetingContextProjectionService.BuildAsync(request, ct);
            elapsedMilliseconds[index] = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        }

        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        Array.Sort(elapsedMilliseconds);
        return new ProjectionBaseline(
            Percentile(elapsedMilliseconds, 0.50),
            Percentile(elapsedMilliseconds, 0.95),
            allocatedBytes / SampleCount,
            result!);
    }

    private static double Percentile(double[] sortedValues, double percentile)
    {
        var index = Math.Clamp(
            (int)Math.Ceiling(sortedValues.Length * percentile) - 1,
            0,
            sortedValues.Length - 1);
        return sortedValues[index];
    }

    [TestMethod]
    [TestCategory("Performance")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task RecoveryAndSummaryCache_20Participants50Rounds_RecordsBaseline()
    {
        const int cacheLookupCount = 250;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.CancellationToken);
        await SqliteSchema.EnsureCreatedAsync(connection, TestContext.CancellationToken);
        var sessionId = Guid.NewGuid().ToString("N");
        await SeedLongMeetingAsync(connection, sessionId, TestContext.CancellationToken);
        var repository = new SqliteMeetingRepository(connection);

        for (var index = 0; index < 3; index++)
        {
            _ = await repository.GetMeetingSnapshotAsync(
                sessionId,
                TestContext.CancellationToken);
        }

        var tracedStatements = 0;
        SQLitePCL.strdelegate_trace traceCallback = (_, _) =>
            Interlocked.Increment(ref tracedStatements);
        SQLitePCL.raw.sqlite3_trace(connection.Handle, traceCallback, null);

        Interlocked.Exchange(ref tracedStatements, 0);
        var recoveryBaseline = await MeasureRecoveryAsync(
            repository,
            sessionId,
            TestContext.CancellationToken);
        var recoveryStatementCount = Volatile.Read(ref tracedStatements);

        Interlocked.Exchange(ref tracedStatements, 0);
        var cacheBaseline = await MeasureSummaryCacheAsync(
            repository,
            sessionId,
            cacheLookupCount,
            TestContext.CancellationToken);
        var cacheStatementCount = Volatile.Read(ref tracedStatements);

        SQLitePCL.raw.sqlite3_trace(
            connection.Handle,
            (SQLitePCL.strdelegate_trace)null!,
            null);
        GC.KeepAlive(traceCallback);

        TestContext.WriteLine(
            "Meeting recovery baseline: participants={0}; rounds={1}; "
            + "p50={2:F3} ms; p95={3:F3} ms; allocated={4:N0} B/op; "
            + "SQLite statements={5:F1}/resume. Summary cache: lookups={6}; "
            + "hit-rate={7:P1}; p50={8:F3} ms; p95={9:F3} ms; statements={10}.",
            ParticipantCount,
            LongMeetingRoundCount,
            recoveryBaseline.P50Milliseconds,
            recoveryBaseline.P95Milliseconds,
            recoveryBaseline.AllocatedBytesPerOperation,
            recoveryStatementCount / (double)SampleCount,
            cacheLookupCount,
            cacheBaseline.HitCount / (double)cacheLookupCount,
            cacheBaseline.P50Milliseconds,
            cacheBaseline.P95Milliseconds,
            cacheStatementCount);

        Assert.IsNotNull(recoveryBaseline.Snapshot);
        Assert.AreEqual(LongMeetingRoundCount, recoveryBaseline.Snapshot.Session.CurrentRound);
        Assert.HasCount(ParticipantCount, recoveryBaseline.Snapshot.Participants);
        Assert.IsNotNull(recoveryBaseline.Snapshot.NextScheduledInvocation);
        Assert.IsNotNull(recoveryBaseline.Snapshot.LatestSummary);
        Assert.AreEqual(5 * SampleCount, recoveryStatementCount);
        Assert.AreEqual(cacheLookupCount, cacheBaseline.HitCount);
        Assert.AreEqual(cacheLookupCount, cacheStatementCount);
        Assert.IsLessThan(1_000, recoveryBaseline.P95Milliseconds);
        Assert.IsLessThan(250, cacheBaseline.P95Milliseconds);
    }

    private static async Task<PersistenceBaseline> MeasureRecoveryAsync(
        SqliteMeetingRepository repository,
        string sessionId,
        CancellationToken ct)
    {
        var elapsedMilliseconds = new double[SampleCount];
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        MeetingSnapshot? snapshot = null;

        for (var index = 0; index < SampleCount; index++)
        {
            var startedAt = Stopwatch.GetTimestamp();
            snapshot = await repository.GetMeetingSnapshotAsync(sessionId, ct);
            elapsedMilliseconds[index] = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        }

        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        Array.Sort(elapsedMilliseconds);
        return new PersistenceBaseline(
            Percentile(elapsedMilliseconds, 0.50),
            Percentile(elapsedMilliseconds, 0.95),
            allocatedBytes / SampleCount,
            snapshot);
    }

    private static async Task<CacheBaseline> MeasureSummaryCacheAsync(
        SqliteMeetingRepository repository,
        string sessionId,
        int lookupCount,
        CancellationToken ct)
    {
        var elapsedMilliseconds = new double[lookupCount];
        var hitCount = 0;
        for (var index = 0; index < lookupCount; index++)
        {
            var roundIndex = index % LongMeetingRoundCount + 1;
            var startedAt = Stopwatch.GetTimestamp();
            var summary = await repository.GetRoundSummaryAsync(sessionId, roundIndex, ct);
            elapsedMilliseconds[index] = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            if (summary is not null)
            {
                hitCount++;
            }
        }

        Array.Sort(elapsedMilliseconds);
        return new CacheBaseline(
            Percentile(elapsedMilliseconds, 0.50),
            Percentile(elapsedMilliseconds, 0.95),
            hitCount);
    }

    private static async Task SeedLongMeetingAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUniversalTime()
            .ToString("O", CultureInfo.InvariantCulture);
        using var transaction = connection.BeginTransaction();

        await using (var session = connection.CreateCommand())
        {
            session.Transaction = transaction;
            session.CommandText = """
                INSERT INTO sessions(session_id, mode, status, created_at, updated_at)
                VALUES($sessionId, 'Meeting', 'Active', $now, $now);
                INSERT INTO meeting_sessions(
                    session_id, run_id, status, current_round,
                    policy_json, policy_hash, selection_version,
                    created_at, updated_at)
                VALUES(
                    $sessionId, 'long-run', 'Running', $roundCount,
                    '{}', 'performance-policy', 1, $now, $now);
                """;
            session.Parameters.AddWithValue("$sessionId", sessionId);
            session.Parameters.AddWithValue("$roundCount", LongMeetingRoundCount);
            session.Parameters.AddWithValue("$now", now);
            await session.ExecuteNonQueryAsync(ct);
        }

        for (var participantIndex = 0; participantIndex < ParticipantCount; participantIndex++)
        {
            await using var participant = connection.CreateCommand();
            participant.Transaction = transaction;
            participant.CommandText = """
                INSERT INTO meeting_participants(
                    session_id, participant_id, agent_id, display_name,
                    join_order, status, joined_selection_version,
                    joined_at, updated_at)
                VALUES(
                    $sessionId, $participantId, $agentId, $displayName,
                    $joinOrder, 'active', 1, $now, $now);
                """;
            participant.Parameters.AddWithValue("$sessionId", sessionId);
            participant.Parameters.AddWithValue("$participantId", $"participant-{participantIndex}");
            participant.Parameters.AddWithValue("$agentId", $"agent-{participantIndex}");
            participant.Parameters.AddWithValue("$displayName", $"Participant {participantIndex}");
            participant.Parameters.AddWithValue("$joinOrder", participantIndex);
            participant.Parameters.AddWithValue("$now", now);
            await participant.ExecuteNonQueryAsync(ct);
        }

        for (var roundIndex = 1; roundIndex <= LongMeetingRoundCount; roundIndex++)
        {
            var invocationId = $"invocation-{roundIndex}";
            await using var round = connection.CreateCommand();
            round.Transaction = transaction;
            round.CommandText = """
                INSERT INTO meeting_rounds(
                    session_id, round_index, run_id, status,
                    first_invocation_id, summary_message_id,
                    summarizes_through_seq, summary_policy_hash,
                    summary_agent_id, summary_invocation_id,
                    created_at, started_at, completed_at)
                VALUES(
                    $sessionId, $roundIndex, 'long-run', $roundStatus,
                    $invocationId, $summaryMessageId,
                    $summarizesThroughSeq, 'performance-policy',
                    'meeting.summarizer', $summaryInvocationId,
                    $now, $now, $completedAt);
                INSERT INTO meeting_invocations(
                    invocation_id, session_id, run_id, round_index, ordinal,
                    participant_id, agent_id, role, status,
                    selection_version, retry_count, message_id,
                    scheduled_at, started_at, completed_at)
                VALUES(
                    $invocationId, $sessionId, 'long-run', $roundIndex, 0,
                    $participantId, $agentId, 'participant', $invocationStatus,
                    1, 0, $messageId, $now, $startedAt, $completedAt);
                """;
            round.Parameters.AddWithValue("$sessionId", sessionId);
            round.Parameters.AddWithValue("$roundIndex", roundIndex);
            round.Parameters.AddWithValue("$roundStatus",
                roundIndex == LongMeetingRoundCount ? "Running" : "Completed");
            round.Parameters.AddWithValue("$invocationId", invocationId);
            round.Parameters.AddWithValue("$summaryMessageId", $"summary-message-{roundIndex}");
            round.Parameters.AddWithValue("$summarizesThroughSeq", roundIndex * 2L);
            round.Parameters.AddWithValue("$summaryInvocationId", $"summary-invocation-{roundIndex}");
            round.Parameters.AddWithValue("$participantId",
                $"participant-{(roundIndex - 1) % ParticipantCount}");
            round.Parameters.AddWithValue("$agentId",
                $"agent-{(roundIndex - 1) % ParticipantCount}");
            round.Parameters.AddWithValue("$invocationStatus",
                roundIndex == LongMeetingRoundCount ? "Scheduled" : "Completed");
            round.Parameters.AddWithValue("$messageId",
                roundIndex == LongMeetingRoundCount ? DBNull.Value : $"message-{roundIndex}");
            round.Parameters.AddWithValue("$startedAt",
                roundIndex == LongMeetingRoundCount ? DBNull.Value : now);
            round.Parameters.AddWithValue("$completedAt",
                roundIndex == LongMeetingRoundCount ? DBNull.Value : now);
            round.Parameters.AddWithValue("$now", now);
            await round.ExecuteNonQueryAsync(ct);
        }

        transaction.Commit();
    }

    private sealed record ProjectionFixture(MeetingProjectionRequest Request);

    private sealed record ProjectionBaseline(
        double P50Milliseconds,
        double P95Milliseconds,
        long AllocatedBytesPerOperation,
        MeetingProjectionResult Result);

    private sealed record PersistenceBaseline(
        double P50Milliseconds,
        double P95Milliseconds,
        long AllocatedBytesPerOperation,
        MeetingSnapshot? Snapshot);

    private sealed record CacheBaseline(
        double P50Milliseconds,
        double P95Milliseconds,
        int HitCount);
}
