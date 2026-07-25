using System.Globalization;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Persistence.Files;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Tests;

[TestClass]
public sealed class ConversationStoreTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task AppendMessageAsync_ConcurrentDrafts_AssignsUniqueContiguousSequences()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            var store = new ConversationStore(dataDirectory);
            var appends = Enumerable.Range(0, 64)
                .Select(index => store.AppendMessageAsync(
                    "session-concurrent",
                    CreateDraft($"message-{index}"),
                    TestContext.CancellationToken))
                .ToArray();

            var appended = await Task.WhenAll(appends);
            var page = await store.ReadPageAsync(
                "session-concurrent",
                cursor: null,
                pageSize: 64,
                TestContext.CancellationToken);

            CollectionAssert.AreEqual(
                Enumerable.Range(1, 64).Select(static value => (long)value).ToArray(),
                appended.Select(static record => record.Sequence).Order().ToArray());
            Assert.AreEqual(64, appended.Select(static record => record.MessageId).Distinct().Count());
            CollectionAssert.AreEqual(
                Enumerable.Range(1, 64).Select(static value => (long)value).ToArray(),
                page.Select(static record => record.Sequence).ToArray());
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [TestMethod]
    [DataRow(ConversationStoreFailurePoint.BeforeFileAppend, 0, false)]
    [DataRow(ConversationStoreFailurePoint.AfterRecordPrefix, 0, true)]
    [DataRow(ConversationStoreFailurePoint.AfterRecordBytes, 1, false)]
    [DataRow(ConversationStoreFailurePoint.AfterLineTerminator, 1, false)]
    [DataRow(ConversationStoreFailurePoint.AfterFileFlush, 1, false)]
    [DataRow(ConversationStoreFailurePoint.BeforeIndexWrite, 1, false)]
    [DataRow(ConversationStoreFailurePoint.AfterIndexWrite, 1, false)]
    public async Task AppendMessageAsync_InjectedFailure_RecoversCanonicalHistory(
        ConversationStoreFailurePoint failurePoint,
        int expectedCommittedRecords,
        bool expectedRepair)
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            var injectionCount = 0;
            var store = new ConversationStore(dataDirectory, new ConversationStoreOptions
            {
                FailureInjector = (point, _) =>
                {
                    if (point == failurePoint && Interlocked.Exchange(ref injectionCount, 1) == 0)
                    {
                        return ValueTask.FromException(
                            new InvalidOperationException("Injected conversation-store failure."));
                    }

                    return ValueTask.CompletedTask;
                }
            });

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await store.AppendMessageAsync(
                    "session-crash",
                    CreateDraft("first"),
                    TestContext.CancellationToken));

            var recoveredStore = new ConversationStore(dataDirectory);
            var repaired = await recoveredStore.RepairIfNeededAsync(
                "session-crash",
                TestContext.CancellationToken);
            var recovered = await recoveredStore.ReadPageAsync(
                "session-crash",
                cursor: null,
                pageSize: 10,
                TestContext.CancellationToken);
            var next = await recoveredStore.AppendMessageAsync(
                "session-crash",
                CreateDraft("next"),
                TestContext.CancellationToken);

            Assert.AreEqual(expectedRepair, repaired);
            Assert.HasCount(expectedCommittedRecords, recovered);
            Assert.AreEqual(expectedCommittedRecords + 1L, next.Sequence);
            Assert.AreEqual(
                expectedCommittedRecords + 1L,
                await recoveredStore.GetLastSequenceAsync(
                    "session-crash",
                    TestContext.CancellationToken));
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [TestMethod]
    public async Task AppendMessageAsync_StableMessageReplay_PreservesOriginalCanonicalRecord()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            var store = new ConversationStore(dataDirectory);
            var original = CreateRecord("stable-message", 1, "original");
            var replay = CreateRecord("stable-message", 2, "different replay output");

            await store.AppendMessageAsync(
                "session-idempotent",
                "Meeting",
                original,
                TestContext.CancellationToken);
            await store.AppendMessageAsync(
                "session-idempotent",
                "Meeting",
                replay,
                TestContext.CancellationToken);

            var history = await store.ReadAllAsync(
                "session-idempotent",
                TestContext.CancellationToken);
            var found = await store.FindMessageByIdAsync(
                "session-idempotent",
                "stable-message",
                TestContext.CancellationToken);

            Assert.HasCount(1, history);
            Assert.IsNotNull(found);
            Assert.AreEqual(1L, found.Sequence);
            Assert.AreEqual("original", ReadText(found));

            var collision = CreateRecord(
                "stable-message",
                2,
                "collision",
                invocationId: "different-invocation");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await store.AppendMessageAsync(
                    "session-idempotent",
                    "Meeting",
                    collision,
                    TestContext.CancellationToken));
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [TestMethod]
    public async Task AppendMessageAsync_ReplayAfterIndexWriteFailure_DoesNotDuplicateJsonl()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            var injectionCount = 0;
            var failingStore = new ConversationStore(dataDirectory, new ConversationStoreOptions
            {
                FailureInjector = (point, _) =>
                {
                    if (point == ConversationStoreFailurePoint.BeforeIndexWrite
                        && Interlocked.Exchange(ref injectionCount, 1) == 0)
                    {
                        return ValueTask.FromException(
                            new InvalidOperationException("Injected index failure."));
                    }

                    return ValueTask.CompletedTask;
                }
            });
            var original = CreateRecord("stable-after-crash", 1, "committed before crash");

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await failingStore.AppendMessageAsync(
                    "session-index-crash",
                    "Meeting",
                    original,
                    TestContext.CancellationToken));

            var recoveredStore = new ConversationStore(dataDirectory);
            await recoveredStore.AppendMessageAsync(
                "session-index-crash",
                "Meeting",
                CreateRecord("stable-after-crash", 2, "replayed output"),
                TestContext.CancellationToken);
            var history = await recoveredStore.ReadAllAsync(
                "session-index-crash",
                TestContext.CancellationToken);
            var jsonlLines = await File.ReadAllLinesAsync(
                Path.Combine(dataDirectory, "messages", "session-index-crash.jsonl"),
                Encoding.UTF8,
                TestContext.CancellationToken);

            Assert.HasCount(1, history);
            Assert.AreEqual("committed before crash", ReadText(history[0]));
            Assert.HasCount(2, jsonlLines);
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [TestMethod]
    public async Task FindMessagesByInvocationIdAsync_AfterIndexRebuild_ReturnsCanonicalRecordsInSequence()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            var store = new ConversationStore(dataDirectory);
            await store.AppendMessageAsync(
                "session-invocation-lookup",
                "Meeting",
                CreateRecord("message-1", 1, "first", "invocation-target"),
                TestContext.CancellationToken);
            await store.AppendMessageAsync(
                "session-invocation-lookup",
                "Meeting",
                CreateRecord("message-2", 2, "other", "invocation-other"),
                TestContext.CancellationToken);
            await store.AppendMessageAsync(
                "session-invocation-lookup",
                "Meeting",
                CreateRecord("message-3", 3, "second", "invocation-target"),
                TestContext.CancellationToken);

            await using (var connection = new SqliteConnection(
                $"Data Source={Path.Combine(dataDirectory, "state.db")};Pooling=False"))
            {
                await connection.OpenAsync(TestContext.CancellationToken);
                await using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM message_index WHERE session_id = 'session-invocation-lookup';";
                await delete.ExecuteNonQueryAsync(TestContext.CancellationToken);
            }

            var recoveredStore = new ConversationStore(dataDirectory);
            var records = await recoveredStore.FindMessagesByInvocationIdAsync(
                "session-invocation-lookup",
                "invocation-target",
                TestContext.CancellationToken);
            var missing = await recoveredStore.FindMessagesByInvocationIdAsync(
                "session-invocation-lookup",
                "invocation-missing",
                TestContext.CancellationToken);

            Assert.HasCount(2, records);
            Assert.AreEqual(1L, records[0].Sequence);
            Assert.AreEqual("first", ReadText(records[0]));
            Assert.AreEqual(3L, records[1].Sequence);
            Assert.AreEqual("second", ReadText(records[1]));
            Assert.IsEmpty(missing);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [TestMethod]
    public async Task AppendMessageAsync_WhenSessionQueueIsFull_RejectsAdditionalWriter()
    {
        var dataDirectory = CreateTemporaryDirectory();
        var injectorReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInjector = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var store = new ConversationStore(dataDirectory, new ConversationStoreOptions
            {
                MaxPendingWritesPerSession = 1,
                FailureInjector = (point, _) => point == ConversationStoreFailurePoint.BeforeFileAppend
                    ? WaitAtFailurePointAsync(injectorReached, releaseInjector)
                    : ValueTask.CompletedTask
            });
            var firstAppend = store.AppendMessageAsync(
                "session-bounded",
                CreateDraft("first"),
                TestContext.CancellationToken);
            await injectorReached.Task.WaitAsync(TestContext.CancellationToken);

            var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await store.AppendMessageAsync(
                    "session-bounded",
                    CreateDraft("overflow"),
                    TestContext.CancellationToken));

            Assert.Contains("queue", exception.Message, StringComparison.OrdinalIgnoreCase);
            releaseInjector.TrySetResult();
            Assert.AreEqual(1, (await firstAppend).Sequence);
        }
        finally
        {
            releaseInjector.TrySetResult();
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [TestMethod]
    public async Task AppendMessageAsync_DifferentSessions_DoNotShareWriteGate()
    {
        var dataDirectory = CreateTemporaryDirectory();
        var injectorReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInjector = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var injectionCount = 0;
            var store = new ConversationStore(dataDirectory, new ConversationStoreOptions
            {
                FailureInjector = (point, _) =>
                {
                    if (point == ConversationStoreFailurePoint.BeforeFileAppend
                        && Interlocked.Increment(ref injectionCount) == 1)
                    {
                        return WaitAtFailurePointAsync(injectorReached, releaseInjector);
                    }

                    return ValueTask.CompletedTask;
                }
            });
            var blockedAppend = store.AppendMessageAsync(
                "session-a",
                CreateDraft("blocked"),
                TestContext.CancellationToken);
            await injectorReached.Task.WaitAsync(TestContext.CancellationToken);

            var independentAppend = await store.AppendMessageAsync(
                    "session-b",
                    CreateDraft("independent"),
                    TestContext.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);

            Assert.AreEqual(1, independentAppend.Sequence);
            releaseInjector.TrySetResult();
            Assert.AreEqual(1, (await blockedAppend).Sequence);
        }
        finally
        {
            releaseInjector.TrySetResult();
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [TestMethod]
    public async Task ReadPageAsync_WhenIndexHasGap_RebuildsFromCanonicalJsonl()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            var store = new ConversationStore(dataDirectory);
            for (var index = 0; index < 3; index++)
            {
                await store.AppendMessageAsync(
                    "session-index-rebuild",
                    CreateDraft($"message-{index}"),
                    TestContext.CancellationToken);
            }

            await ExecuteIndexCommandAsync(
                dataDirectory,
                "DELETE FROM message_index WHERE session_id = 'session-index-rebuild' AND sequence = 2;",
                TestContext.CancellationToken);

            var page = await store.ReadPageAsync(
                "session-index-rebuild",
                cursor: null,
                pageSize: 3,
                TestContext.CancellationToken);

            CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, page.Select(static item => item.Sequence).ToArray());
            Assert.AreEqual(
                3L,
                await ExecuteIndexScalarAsync(
                    dataDirectory,
                    "SELECT COUNT(*) FROM message_index WHERE session_id = 'session-index-rebuild';",
                    TestContext.CancellationToken));
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [TestMethod]
    public async Task ReadPageAsync_WhenIndexPointsOutsideJsonl_ReportsCorruption()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            var store = new ConversationStore(dataDirectory);
            await store.AppendMessageAsync(
                "session-index-corrupt",
                CreateDraft("message"),
                TestContext.CancellationToken);
            await ExecuteIndexCommandAsync(
                dataDirectory,
                "UPDATE message_index SET file_offset = 999999 WHERE session_id = 'session-index-corrupt';",
                TestContext.CancellationToken);

            var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(
                async () => await store.ReadPageAsync(
                    "session-index-corrupt",
                    cursor: null,
                    pageSize: 1,
                    TestContext.CancellationToken));

            Assert.Contains("outside canonical JSONL", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [TestMethod]
    public async Task RebuildIndexesAsync_RestoresSessionAndMessageIndexesFromJsonlHeader()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            var store = new ConversationStore(dataDirectory);
            await store.AppendMessageAsync("session-full-rebuild", "Expert", CreateDraft("first"), TestContext.CancellationToken);
            await store.AppendMessageAsync("session-full-rebuild", "Expert", CreateDraft("second"), TestContext.CancellationToken);

            await ExecuteIndexCommandAsync(
                dataDirectory,
                "DELETE FROM message_index;",
                TestContext.CancellationToken);
            await ExecuteIndexCommandAsync(
                dataDirectory,
                "DELETE FROM sessions;",
                TestContext.CancellationToken);

            var result = await store.RebuildIndexesAsync(TestContext.CancellationToken);

            Assert.AreEqual(1, result.SessionsScanned);
            Assert.AreEqual(1, result.SessionsRebuilt);
            Assert.AreEqual(2, result.MessagesUpserted);
            Assert.AreEqual(
                1L,
                await ExecuteIndexScalarAsync(
                    dataDirectory,
                    "SELECT COUNT(*) FROM sessions WHERE session_id = 'session-full-rebuild';",
                    TestContext.CancellationToken));
            Assert.AreEqual(
                "Expert",
                await ExecuteIndexTextScalarAsync(
                    dataDirectory,
                    "SELECT mode FROM sessions WHERE session_id = 'session-full-rebuild';",
                    TestContext.CancellationToken));
            Assert.AreEqual(
                "Recovered",
                await ExecuteIndexTextScalarAsync(
                    dataDirectory,
                    "SELECT status FROM sessions WHERE session_id = 'session-full-rebuild';",
                    TestContext.CancellationToken));
            Assert.AreEqual(
                2L,
                await ExecuteIndexScalarAsync(
                    dataDirectory,
                    "SELECT COUNT(*) FROM message_index WHERE session_id = 'session-full-rebuild';",
                    TestContext.CancellationToken));
            Assert.IsNotNull(result.BackupPath);
            Assert.IsTrue(Directory.Exists(result.BackupPath));
            Assert.IsTrue(File.Exists(Path.Combine(result.BackupPath, "state.db")));
            CollectionAssert.Contains(result.RecoverableItems,
                "Session 'session-full-rebuild': restored Session metadata and 2 message index row(s).");
            CollectionAssert.Contains(result.UnrecoverableDiagnostics,
                "Run state cannot be reconstructed from JSONL alone.");
            CollectionAssert.Contains(result.UnrecoverableDiagnostics,
                "Tool Grants cannot be reconstructed from JSONL alone.");
            CollectionAssert.Contains(result.UnrecoverableDiagnostics,
                "Tool intents cannot be reconstructed from JSONL alone.");
            CollectionAssert.Contains(result.UnrecoverableDiagnostics,
                "Invocation snapshots cannot be reconstructed from JSONL alone.");
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [TestMethod]
    public async Task OversizedText_RoundTripsThroughBoundedBlobReadModes()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            var originalText = new string('x', 4096);
            var store = new ConversationStore(dataDirectory, new ConversationStoreOptions { MaxJsonLineBytes = 1024 });

            await store.AppendMessageAsync("session-blob-read-modes", CreateDraft(originalText), TestContext.CancellationToken);

            var metadataPage = await store.ReadPageAsync("session-blob-read-modes", cursor: null, pageSize: 1, ct: TestContext.CancellationToken);
            Assert.HasCount(1, metadataPage);
            var contentArray = metadataPage[0].Content;
            var chunk = contentArray.EnumerateArray().Single();
            Assert.AreEqual("blob_ref", chunk.GetProperty("type").GetString());
            var blob = chunk.GetProperty("blob");
            var blobId = blob.GetProperty("blobId").GetString();
            Assert.IsNotNull(blobId);
            Assert.AreEqual(4096L, blob.GetProperty("length").GetInt64());
            Assert.IsNotNull(blob.GetProperty("sha256").GetString());
            Assert.IsNotNull(blob.GetProperty("contentType").GetString());
            Assert.IsNotNull(blob.GetProperty("accessScope").GetString());

            var streamPage = await store.ReadPageAsync("session-blob-read-modes", cursor: null, pageSize: 1, options: new ConversationReadOptions(ConversationBlobReadMode.Stream, MaxExpandedBlobBytes: 8192), ct: TestContext.CancellationToken);
            Assert.HasCount(1, streamPage);
            var streamChunk = streamPage[0].Content.EnumerateArray().Single();
            Assert.AreEqual("text", streamChunk.GetProperty("type").GetString());
            Assert.AreEqual(originalText, streamChunk.GetProperty("text").GetString());

            var omitPage = await store.ReadPageAsync("session-blob-read-modes", cursor: null, pageSize: 1, options: new ConversationReadOptions(ConversationBlobReadMode.Omit), ct: TestContext.CancellationToken);
            Assert.HasCount(1, omitPage);
            var omitChunk = omitPage[0].Content.EnumerateArray().Single();
            Assert.AreEqual($"[blob omitted: {blobId}]", omitChunk.GetProperty("text").GetString());

            var smallBufferPage = await store.ReadPageAsync("session-blob-read-modes", cursor: null, pageSize: 1, options: new ConversationReadOptions(ConversationBlobReadMode.Stream, MaxExpandedBlobBytes: 4095), ct: TestContext.CancellationToken);
            Assert.HasCount(1, smallBufferPage);
            var smallBufferChunk = smallBufferPage[0].Content.EnumerateArray().Single();
            Assert.AreEqual($"[blob omitted: {blobId}]", smallBufferChunk.GetProperty("text").GetString());

            var lines = await File.ReadAllLinesAsync(Path.Combine(dataDirectory, "messages", "session-blob-read-modes.jsonl"), Encoding.UTF8, TestContext.CancellationToken);
            foreach (var line in lines)
            {
                Assert.IsLessThanOrEqualTo(1024, Encoding.UTF8.GetByteCount(line));
            }
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [TestMethod]
    public async Task PrepareSessionDeletionAsync_AfterArtifactsMoveFailure_RestoresAllLiveFiles()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            const string sessionId = "session-delete-recovery";
            var store = new ConversationStore(
                dataDirectory,
                new ConversationStoreOptions { MaxJsonLineBytes = 1024 });
            await store.AppendMessageAsync(
                sessionId,
                CreateDraft(new string('r', 4096)),
                TestContext.CancellationToken);
            var metadata = await store.GetHistoryMetadataAsync(
                sessionId,
                TestContext.CancellationToken);
            var blobId = Assert.ContainsSingle(metadata.BlobIds);
            var canonicalPath = Path.Combine(dataDirectory, "messages", sessionId + ".jsonl");
            var compactPath = Path.Combine(
                dataDirectory,
                "messages",
                sessionId + ".compact.json");
            var blobPath = Path.Combine(dataDirectory, "blobs", blobId + ".blob");
            await File.WriteAllTextAsync(
                compactPath,
                "{\"projection\":true}",
                TestContext.CancellationToken);
            var injectionCount = 0;
            var failingStore = new ConversationStore(dataDirectory, new ConversationStoreOptions
            {
                FailureInjector = (point, _) =>
                {
                    if (point == ConversationStoreFailurePoint.AfterDeletionArtifactsMove
                        && Interlocked.Exchange(ref injectionCount, 1) == 0)
                    {
                        return ValueTask.FromException(
                            new InvalidOperationException("Injected Session deletion failure."));
                    }

                    return ValueTask.CompletedTask;
                }
            });

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await failingStore.PrepareSessionDeletionAsync(
                    sessionId,
                    includeBlobs: true,
                    TestContext.CancellationToken));

            Assert.IsTrue(File.Exists(canonicalPath));
            Assert.IsTrue(File.Exists(compactPath));
            Assert.IsTrue(File.Exists(blobPath));
            var recoveryRoot = Path.Combine(
                dataDirectory,
                "recovery",
                "session-deletions");
            var manifest = Assert.ContainsSingle(
                Directory.EnumerateFiles(recoveryRoot, "manifest.json", SearchOption.AllDirectories));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(
                manifest,
                TestContext.CancellationToken));
            Assert.AreEqual("restored", json.RootElement.GetProperty("status").GetString());
            Assert.Contains("Injected Session deletion failure", json.RootElement
                .GetProperty("error")
                .GetString()!);
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [TestMethod]
    public async Task CompactCache_WriteReadRoundTripAndCorruptionIsIgnored()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            const string sessionId = "session-compact-cache";
            var store = new ConversationStore(dataDirectory);
            await store.AppendMessageAsync(
                sessionId,
                CreateDraft("canonical compact source"),
                TestContext.CancellationToken);
            var canonicalPath = Path.Combine(
                dataDirectory,
                "messages",
                sessionId + ".jsonl");
            var canonicalBefore = await File.ReadAllBytesAsync(
                canonicalPath,
                TestContext.CancellationToken);
            var source = await store.ReadCompactionSourceAsync(
                sessionId,
                TestContext.CancellationToken);
            var projectedContent = source.Records[0].Content.Clone();
            var cache = new ConversationCompactCacheV1(
                ConversationCompactCacheV1.CurrentSchema,
                sessionId,
                source.HistorySha256,
                "summary",
                "provider-a",
                "model-a",
                KeepLastTokens: null,
                source.Records.Count,
                DroppedMessageCount: source.Records.Count - 1,
                BeforeEstimatedTokens: 12,
                AfterEstimatedTokens: 3,
                "provider.estimated",
                [new ConversationCompactMessageV1("system", projectedContent)],
                DateTimeOffset.UtcNow);

            await store.WriteCompactCacheAsync(
                sessionId,
                cache,
                TestContext.CancellationToken);
            var restored = await store.ReadCompactCacheAsync(
                sessionId,
                TestContext.CancellationToken);

            Assert.IsNotNull(restored);
            Assert.AreEqual(cache.SourceHistorySha256, restored.SourceHistorySha256);
            Assert.AreEqual(cache.Strategy, restored.Strategy);
            Assert.AreEqual(cache.ProviderId, restored.ProviderId);
            Assert.AreEqual(cache.ModelId, restored.ModelId);
            Assert.AreEqual(cache.AfterEstimatedTokens, restored.AfterEstimatedTokens);
            Assert.HasCount(1, restored.ProjectionMessages);
            Assert.AreEqual("system", restored.ProjectionMessages[0].Role);
            CollectionAssert.AreEqual(
                canonicalBefore,
                await File.ReadAllBytesAsync(canonicalPath, TestContext.CancellationToken));

            await File.WriteAllTextAsync(
                Path.Combine(dataDirectory, "messages", sessionId + ".compact.json"),
                "{not-json",
                TestContext.CancellationToken);

            Assert.IsNull(await store.ReadCompactCacheAsync(
                sessionId,
                TestContext.CancellationToken));
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [TestMethod]
    public async Task CompactCache_WhenHistoryChanges_RejectsStaleWriteAndPreservesExistingCache()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            const string sessionId = "session-compact-stale";
            var store = new ConversationStore(dataDirectory);
            await store.AppendMessageAsync(
                sessionId,
                CreateDraft("first compact source"),
                TestContext.CancellationToken);
            var source = await store.ReadCompactionSourceAsync(
                sessionId,
                TestContext.CancellationToken);
            var cache = new ConversationCompactCacheV1(
                ConversationCompactCacheV1.CurrentSchema,
                sessionId,
                source.HistorySha256,
                "full",
                "provider-a",
                "model-a",
                KeepLastTokens: null,
                source.Records.Count,
                DroppedMessageCount: 0,
                BeforeEstimatedTokens: 1,
                AfterEstimatedTokens: 1,
                "provider.estimated",
                [new ConversationCompactMessageV1(
                    source.Records[0].Role,
                    source.Records[0].Content.Clone())],
                DateTimeOffset.UtcNow);
            await store.WriteCompactCacheAsync(
                sessionId,
                cache,
                TestContext.CancellationToken);
            var cachePath = Path.Combine(
                dataDirectory,
                "messages",
                sessionId + ".compact.json");
            var existingBytes = await File.ReadAllBytesAsync(
                cachePath,
                TestContext.CancellationToken);

            await store.AppendMessageAsync(
                sessionId,
                CreateDraft("second compact source"),
                TestContext.CancellationToken);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await store.WriteCompactCacheAsync(
                    sessionId,
                    cache,
                    TestContext.CancellationToken));
            CollectionAssert.AreEqual(
                existingBytes,
                await File.ReadAllBytesAsync(cachePath, TestContext.CancellationToken));
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    private static async ValueTask WaitAtFailurePointAsync(
        TaskCompletionSource reached,
        TaskCompletionSource release)
    {
        reached.TrySetResult();
        await release.Task.ConfigureAwait(false);
    }

    private static async Task ExecuteIndexCommandAsync(
        string dataDirectory,
        string sql,
        CancellationToken ct)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(dataDirectory, "state.db")};Pooling=False");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ExecuteIndexScalarAsync(
        string dataDirectory,
        string sql,
        CancellationToken ct)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(dataDirectory, "state.db")};Pooling=False");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<string> ExecuteIndexTextScalarAsync(
        string dataDirectory,
        string sql,
        CancellationToken ct)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(dataDirectory, "state.db")};Pooling=False");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
            await command.ExecuteScalarAsync(ct),
            CultureInfo.InvariantCulture)
            ?? throw new InvalidDataException("SQLite text scalar was null.");
    }

    private static ConversationMessageDraft CreateDraft(string text)
    {
        var encodedText = JsonEncodedText.Encode(text).ToString();
        using var document = JsonDocument.Parse(
            $"[{{\"type\":\"text\",\"text\":\"{encodedText}\"}}]");
        return new ConversationMessageDraft(
            "invocation-1",
            "agent-1",
            "user",
            document.RootElement.Clone(),
            DateTimeOffset.UtcNow);
    }

    private static ConversationRecordV1 CreateRecord(
        string messageId,
        long sequence,
        string text,
        string invocationId = "stable-invocation")
    {
        var draft = CreateDraft(text);
        return new ConversationRecordV1(
            messageId,
            sequence,
            invocationId,
            "stable-agent",
            "assistant",
            draft.Content,
            DateTimeOffset.UtcNow);
    }

    private static string ReadText(ConversationRecordV1 record) =>
        record.Content.EnumerateArray().Single().GetProperty("text").GetString()
        ?? throw new InvalidDataException("Conversation record text was null.");

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-conversation-store-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
