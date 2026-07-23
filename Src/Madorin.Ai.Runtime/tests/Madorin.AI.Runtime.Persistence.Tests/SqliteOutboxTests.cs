using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Tests;

[TestClass]
public sealed class SqliteOutboxTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task AppendAsync_MultipleEvents_AllocatesMonotonicGsn()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        using var outbox = new SqliteEventOutbox(connection);

        var first = await outbox.AppendAsync(
            "run-1",
            0,
            "run.accepted",
            "{}",
            TestContext.CancellationToken);
        var second = await outbox.AppendAsync(
            "run-2",
            0,
            "run.accepted",
            "{}",
            TestContext.CancellationToken);
        var third = await outbox.AppendAsync(
            "run-1",
            1,
            "output.delta",
            "{}",
            TestContext.CancellationToken);

        Assert.AreEqual(1L, first);
        Assert.AreEqual(2L, second);
        Assert.AreEqual(3L, third);
    }

    [TestMethod]
    public async Task AppendAsync_ConcurrentRuns_KeepGlobalGsnUniqueAndRunSequenceOrdered()
    {
        const int runCount = 10;
        const int eventsPerRun = 20;
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        using var outbox = new SqliteEventOutbox(connection);

        var appendTasks = Enumerable.Range(0, runCount)
            .Select(async runIndex =>
            {
                for (var runSequence = 0; runSequence < eventsPerRun; runSequence++)
                {
                    await outbox.AppendAsync(
                        $"run-{runIndex}",
                        runSequence,
                        "output.delta",
                        $"{{\"run\":{runIndex},\"sequence\":{runSequence}}}",
                        TestContext.CancellationToken);
                }
            });
        await Task.WhenAll(appendTasks);

        var entries = await outbox.LoadPendingAsync(TestContext.CancellationToken);
        Assert.HasCount(runCount * eventsPerRun, entries);
        CollectionAssert.AreEqual(
            Enumerable.Range(1, entries.Count).Select(static value => (long)value).ToArray(),
            entries.Select(static entry => entry.Gsn).ToArray());
        foreach (var runGroup in entries.GroupBy(static entry => entry.RunId))
        {
            CollectionAssert.AreEqual(
                Enumerable.Range(0, eventsPerRun).Select(static value => (long)value).ToArray(),
                runGroup.Select(static entry => entry.RunSequence).ToArray(),
                runGroup.Key);
        }
    }

    [TestMethod]
    public async Task TwoOutboxStores_WithSameRunAndGsn_KeepReplayAndAckWatermarksIsolated()
    {
        await using var connectionA = await CreateDatabaseAsync(TestContext.CancellationToken);
        await using var connectionB = await CreateDatabaseAsync(TestContext.CancellationToken);
        using var outboxA = new SqliteEventOutbox(connectionA);
        using var outboxB = new SqliteEventOutbox(connectionB);

        var gsnA = await outboxA.AppendAsync(
            "same-run",
            0,
            "run.accepted",
            "{\"store\":\"a\"}",
            TestContext.CancellationToken);
        var gsnB = await outboxB.AppendAsync(
            "same-run",
            0,
            "run.accepted",
            "{\"store\":\"b\"}",
            TestContext.CancellationToken);
        await outboxA.AcknowledgeAsync(gsnA, TestContext.CancellationToken);

        Assert.AreEqual(1L, gsnA);
        Assert.AreEqual(gsnA, gsnB);
        Assert.IsEmpty(await outboxA.LoadPendingAsync(TestContext.CancellationToken));
        Assert.AreEqual(gsnA, await outboxA.GetAcknowledgedGsnAsync(TestContext.CancellationToken));
        var pendingB = await outboxB.LoadPendingAsync(TestContext.CancellationToken);
        Assert.HasCount(1, pendingB);
        Assert.AreEqual("{\"store\":\"b\"}", pendingB[0].PayloadJson);
        Assert.AreEqual(0L, await outboxB.GetAcknowledgedGsnAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task LoadPendingAsync_AfterOutboxRestart_ReturnsPendingAndSentInGsnOrder()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        long firstGsn;
        using (var originalOutbox = new SqliteEventOutbox(connection))
        {
            firstGsn = await originalOutbox.AppendAsync(
                "run-1",
                0,
                "run.accepted",
                "{\"kind\":\"first\"}",
                TestContext.CancellationToken);
            await originalOutbox.MarkSentAsync(firstGsn, TestContext.CancellationToken);
            await originalOutbox.AppendAsync(
                "run-2",
                0,
                "run.accepted",
                "{\"kind\":\"second\"}",
                TestContext.CancellationToken);
        }

        using var restartedOutbox = new SqliteEventOutbox(connection);
        var entries = await restartedOutbox.LoadPendingAsync(TestContext.CancellationToken);

        Assert.HasCount(2, entries);
        Assert.AreEqual(firstGsn, entries[0].Gsn);
        Assert.AreEqual(firstGsn + 1, entries[1].Gsn);
    }

    [TestMethod]
    public async Task AcknowledgeAsync_ThroughWatermark_ExcludesAcknowledgedEntries()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        using var outbox = new SqliteEventOutbox(connection);
        var first = await outbox.AppendAsync(
            "run-1",
            0,
            "run.accepted",
            "{}",
            TestContext.CancellationToken);
        var second = await outbox.AppendAsync(
            "run-1",
            1,
            "output.delta",
            "{}",
            TestContext.CancellationToken);
        var third = await outbox.AppendAsync(
            "run-1",
            2,
            "run.completed",
            "{}",
            TestContext.CancellationToken);

        await outbox.AcknowledgeAsync(second, TestContext.CancellationToken);
        var entries = await outbox.LoadPendingAsync(TestContext.CancellationToken);
        var acknowledgedGsn = await outbox.GetAcknowledgedGsnAsync(TestContext.CancellationToken);

        Assert.AreEqual(second, acknowledgedGsn);
        Assert.HasCount(1, entries);
        Assert.AreEqual(third, entries[0].Gsn);
        Assert.IsGreaterThan(first, acknowledgedGsn);
    }

    [TestMethod]
    public async Task ContainsAsync_RequiresExactRunTypeAndPayloadMatch()
    {
        const string payload = "{\"decision\":\"timeout\"}";
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        using var outbox = new SqliteEventOutbox(connection);
        await outbox.AppendAsync(
            "run-target",
            0,
            "meeting.hitl.response",
            payload,
            TestContext.CancellationToken);

        Assert.IsTrue(await outbox.ContainsAsync(
            "run-target",
            "meeting.hitl.response",
            payload,
            TestContext.CancellationToken));
        Assert.IsFalse(await outbox.ContainsAsync(
            "run-other",
            "meeting.hitl.response",
            payload,
            TestContext.CancellationToken));
        Assert.IsFalse(await outbox.ContainsAsync(
            "run-target",
            "meeting.hitl.request",
            payload,
            TestContext.CancellationToken));
        Assert.IsFalse(await outbox.ContainsAsync(
            "run-target",
            "meeting.hitl.response",
            "{\"decision\":\"timeout \"}",
            TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task AppendAsync_WhenInsertFailsBeforeCommit_RollsBackGsnAllocation()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        await using (var trigger = connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER fail_event_outbox_insert
                BEFORE INSERT ON event_outbox
                BEGIN
                    SELECT RAISE(ABORT, 'injected pre-commit failure');
                END;
                """;
            await trigger.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        using var outbox = new SqliteEventOutbox(connection);
        await Assert.ThrowsExactlyAsync<SqliteException>(
            async () => await outbox.AppendAsync(
                "run-failed",
                0,
                "run.accepted",
                "{\"attempt\":1}",
                TestContext.CancellationToken));
        await using (var dropTrigger = connection.CreateCommand())
        {
            dropTrigger.CommandText = "DROP TRIGGER fail_event_outbox_insert;";
            await dropTrigger.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        var committedGsn = await outbox.AppendAsync(
            "run-success",
            0,
            "run.accepted",
            "{\"attempt\":2}",
            TestContext.CancellationToken);

        Assert.AreEqual(1L, committedGsn);
        var pending = await outbox.LoadPendingAsync(TestContext.CancellationToken);
        Assert.HasCount(1, pending);
        Assert.AreEqual("run-success", pending[0].RunId);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task RestartAcrossCommitSendAndAckBoundaries_ReplaysUntilAcknowledged()
    {
        const string payload = "{\"kind\":\"durable\"}";
        var root = Path.Combine(
            Path.GetTempPath(),
            "madorin-runtime-outbox-crash-tests",
            Guid.NewGuid().ToString("N"));
        var replayDirectory = Path.Combine(root, "replay");
        Directory.CreateDirectory(replayDirectory);
        var connectionString = $"Data Source={Path.Combine(root, "state.db")};Pooling=False";
        try
        {
            long gsn;
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(TestContext.CancellationToken);
                await SqliteSchema.EnsureCreatedAsync(
                    connection,
                    TestContext.CancellationToken);
                using var outbox = new SqliteEventOutbox(connection, replayDirectory);
                gsn = await outbox.AppendAsync(
                    "run-1",
                    0,
                    "run.accepted",
                    payload,
                    TestContext.CancellationToken);
            }

            // Restart after commit and before send: the committed envelope must replay.
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(TestContext.CancellationToken);
                using var outbox = new SqliteEventOutbox(connection, replayDirectory);
                await AssertSinglePendingAsync(outbox, gsn, payload);
            }

            // Restart after the transport sent the envelope but before MarkSentAsync.
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(TestContext.CancellationToken);
                using var outbox = new SqliteEventOutbox(connection, replayDirectory);
                await AssertSinglePendingAsync(outbox, gsn, payload);
                await outbox.MarkSentAsync(gsn, TestContext.CancellationToken);
            }

            // Restart after marking sent but before host acknowledgement.
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(TestContext.CancellationToken);
                using var outbox = new SqliteEventOutbox(connection, replayDirectory);
                await AssertSinglePendingAsync(outbox, gsn, payload);
                await outbox.AcknowledgeAsync(gsn, TestContext.CancellationToken);
            }

            // Restart after acknowledgement: replay is released but the watermark remains.
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync(TestContext.CancellationToken);
                using var outbox = new SqliteEventOutbox(connection, replayDirectory);
                Assert.IsEmpty(await outbox.LoadPendingAsync(TestContext.CancellationToken));
                Assert.AreEqual(
                    gsn,
                    await outbox.GetAcknowledgedGsnAsync(TestContext.CancellationToken));
                Assert.IsEmpty(Directory.GetFiles(replayDirectory, "*.evt"));
                Assert.AreEqual(
                    gsn + 1,
                    await outbox.AppendAsync(
                        "run-2",
                        0,
                        "run.accepted",
                        "{}",
                        TestContext.CancellationToken));
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task AppendDeltaAsync_WhenReplayQuotaIsReached_DropsBeforeAllocatingGsn()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var replayDirectory = Path.Combine(
            Path.GetTempPath(),
            "madorin-runtime-replay-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(replayDirectory);
        try
        {
            using var outbox = new SqliteEventOutbox(
                connection,
                replayDirectory,
                maxReplayBytes: 3);
            var first = await outbox.AppendAsync(
                "run-1",
                0,
                "run.accepted",
                "{}",
                TestContext.CancellationToken);

            var dropped = await outbox.AppendDeltaAsync(
                "run-1",
                1,
                "output.delta",
                new string('x', 220),
                TestContext.CancellationToken);
            var second = await outbox.AppendAsync(
                "run-1",
                2,
                "run.completed",
                "{}",
                TestContext.CancellationToken);

            Assert.AreEqual(1L, first);
            Assert.IsNull(dropped);
            Assert.AreEqual(2L, second);
            Assert.HasCount(2, await outbox.LoadPendingAsync(TestContext.CancellationToken));
            Assert.HasCount(1, Directory.GetFiles(replayDirectory, "*.evt"));

            await outbox.AcknowledgeAsync(first, TestContext.CancellationToken);
            Assert.HasCount(0, Directory.GetFiles(replayDirectory, "*.evt"));
        }
        finally
        {
            if (Directory.Exists(replayDirectory))
            {
                Directory.Delete(replayDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task AppendAsync_WhenMemoryTierIsFull_SpillsAndRecordsCapacityMetrics()
    {
        await using var connection = await CreateDatabaseAsync(TestContext.CancellationToken);
        var replayDirectory = Path.Combine(
            Path.GetTempPath(),
            "madorin-runtime-replay-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(replayDirectory);
        var measurements = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (string.Equals(
                    instrument.Meter.Name,
                    SqliteEventOutbox.DiagnosticsMeterName,
                    StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, _, tags, _) =>
            {
                measurements.Add(instrument.Name);
                foreach (var tag in tags)
                {
                    Assert.IsTrue(tag.Key is "tier" or "reason");
                }
            });
        listener.Start();

        try
        {
            using var outbox = new SqliteEventOutbox(
                connection,
                replayDirectory,
                maxReplayBytes: 64,
                maxMemoryReplayBytes: 8);
            var first = await outbox.AppendAsync(
                "run-1",
                0,
                "run.accepted",
                "123456",
                TestContext.CancellationToken);
            var second = await outbox.AppendAsync(
                "run-1",
                1,
                "run.completed",
                "abcdef",
                TestContext.CancellationToken);
            var dropped = await outbox.AppendDeltaAsync(
                "run-1",
                2,
                "output.delta",
                new string('x', 64),
                TestContext.CancellationToken);

            Assert.AreEqual(1L, first);
            Assert.AreEqual(2L, second);
            Assert.IsNull(dropped);
            Assert.AreEqual(6L, outbox.MemoryReplayBytes);
            Assert.AreEqual(1L, outbox.MemorySpillCount);
            Assert.AreEqual(1L, outbox.DroppedDeltaCount);
            Assert.AreEqual(2L, outbox.BackpressureCount);
            Assert.IsLessThanOrEqualTo(8L, outbox.MemoryReplayBytes);

            await outbox.AcknowledgeAsync(second, TestContext.CancellationToken);

            Assert.AreEqual(0L, outbox.MemoryReplayBytes);
            Assert.AreEqual(0L, outbox.ReplayBytes);
            Assert.HasCount(0, Directory.GetFiles(replayDirectory, "*.evt"));
            Assert.IsTrue(measurements.Contains("madorin.runtime.outbox.configured_capacity"));
            Assert.IsTrue(measurements.Contains("madorin.runtime.outbox.retained"));
            Assert.IsTrue(measurements.Contains("madorin.runtime.outbox.memory_spill"));
            Assert.IsTrue(measurements.Contains("madorin.runtime.outbox.delta_dropped"));
            Assert.IsTrue(measurements.Contains("madorin.runtime.outbox.backpressure"));
        }
        finally
        {
            if (Directory.Exists(replayDirectory))
            {
                Directory.Delete(replayDirectory, recursive: true);
            }
        }
    }

    private static async Task<SqliteConnection> CreateDatabaseAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await SqliteSchema.EnsureCreatedAsync(connection, ct);
        return connection;
    }

    private async Task AssertSinglePendingAsync(
        SqliteEventOutbox outbox,
        long expectedGsn,
        string expectedPayload)
    {
        var pending = await outbox.LoadPendingAsync(TestContext.CancellationToken);
        Assert.HasCount(1, pending);
        Assert.AreEqual(expectedGsn, pending[0].Gsn);
        Assert.AreEqual(expectedPayload, pending[0].PayloadJson);
    }
}
