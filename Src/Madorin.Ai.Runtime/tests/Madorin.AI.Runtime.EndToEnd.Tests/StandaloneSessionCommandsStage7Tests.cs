using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Cli;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
[DoNotParallelize]
public sealed class StandaloneSessionCommandsStage7Tests
{
    private const string ProviderId = "session-provider";
    private const string ModelId = "session-model";
    private const string AgentId = "session-agent";
    private static readonly string[] MaintenanceAuditPhases =
    [
        "archive:prepared",
        "archive:completed",
        "unarchive:prepared",
        "unarchive:completed",
        "compact:prepared",
        "compact:completed",
        "delete:prepared",
        "delete:completed"
    ];

    private readonly TestContext _testContext;

    public StandaloneSessionCommandsStage7Tests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SessionList_PaginationAndFilters_ReturnExpectedHumanAndJsonResults()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateFixtureAsync(root);
            var firstSessionId = RunSession(fixture, "first private prompt");
            await Task.Delay(TimeSpan.FromMilliseconds(20), _testContext.CancellationToken);
            var secondSessionId = RunSession(fixture, "second private prompt");

            var firstPage = RunCommand(
                fixture,
                "session", "list", "--limit", "1", "--json");

            Assert.AreEqual(ExitCodes.Success, firstPage.ExitCode, firstPage.AllOutput);
            using var firstJson = JsonDocument.Parse(firstPage.Output);
            var firstData = firstJson.RootElement.GetProperty("data");
            var firstSessions = firstData.GetProperty("sessions");
            Assert.AreEqual(1, firstSessions.GetArrayLength());
            var listedFirstId = firstSessions[0].GetProperty("sessionId").GetString();
            var nextCursor = firstData.GetProperty("nextCursor").GetString();
            Assert.IsNotNull(listedFirstId);
            Assert.IsFalse(string.IsNullOrWhiteSpace(nextCursor));

            var secondPage = RunCommand(
                fixture,
                "session", "list", "--limit", "1", "--cursor", nextCursor!, "--json");

            Assert.AreEqual(ExitCodes.Success, secondPage.ExitCode, secondPage.AllOutput);
            using var secondJson = JsonDocument.Parse(secondPage.Output);
            var listedSecondId = secondJson.RootElement
                .GetProperty("data")
                .GetProperty("sessions")[0]
                .GetProperty("sessionId")
                .GetString();
            CollectionAssert.AreEquivalent(
                new[] { firstSessionId, secondSessionId },
                new[] { listedFirstId, listedSecondId });
            Assert.AreEqual(
                JsonValueKind.Null,
                secondJson.RootElement.GetProperty("data").GetProperty("nextCursor").ValueKind);

            var human = RunCommand(fixture, "session", "list", "--status", "active");
            Assert.AreEqual(ExitCodes.Success, human.ExitCode, human.AllOutput);
            Assert.Contains(firstSessionId, human.Output, StringComparison.Ordinal);
            Assert.Contains(secondSessionId, human.Output, StringComparison.Ordinal);
            Assert.Contains("EXPERT", human.Output, StringComparison.Ordinal);

            foreach (var filter in new[]
                     {
                         new[] { "--mode", "meeting" },
                         new[] { "--status", "archived" },
                         new[] { "--search", "missing-title" },
                         new[] { "--since", "2100-01-01T00:00:00Z" }
                     })
            {
                string[] arguments = ["session", "list", .. filter, "--json"];
                var result = RunCommand(fixture, arguments);
                Assert.AreEqual(ExitCodes.Success, result.ExitCode, result.AllOutput);
                using var json = JsonDocument.Parse(result.Output);
                Assert.AreEqual(
                    0,
                    json.RootElement.GetProperty("data").GetProperty("sessions").GetArrayLength());
            }
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SessionShow_WithMessages_ReportsMetadataWithoutMessageContent()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            const string privatePrompt = "private prompt must not appear in summaries";
            const string privateReply = "private reply must not appear in summaries";
            var fixture = await CreateFixtureAsync(root, privateReply);
            var sessionId = RunSession(fixture, privatePrompt);

            var human = RunCommand(fixture, "session", "show", sessionId, "--messages");

            Assert.AreEqual(ExitCodes.Success, human.ExitCode, human.AllOutput);
            Assert.Contains(sessionId, human.Output, StringComparison.Ordinal);
            Assert.Contains("Selection:", human.Output, StringComparison.Ordinal);
            Assert.Contains("Latest Run:", human.Output, StringComparison.Ordinal);
            Assert.Contains("Messages:", human.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(privatePrompt, human.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(privateReply, human.Output, StringComparison.Ordinal);

            var machine = RunCommand(
                fixture,
                "session", "show", sessionId, "--messages", "--json");

            Assert.AreEqual(ExitCodes.Success, machine.ExitCode, machine.AllOutput);
            using var json = JsonDocument.Parse(machine.Output);
            var data = json.RootElement.GetProperty("data");
            Assert.AreEqual(sessionId, data.GetProperty("sessionId").GetString());
            Assert.AreEqual("expert", data.GetProperty("mode").GetString());
            Assert.AreEqual(ProviderId, data
                .GetProperty("selection")
                .GetProperty("providerId")
                .GetString());
            Assert.AreEqual("completed", data
                .GetProperty("latestRun")
                .GetProperty("status")
                .GetString());
            var messages = data.GetProperty("messages");
            Assert.IsGreaterThanOrEqualTo(2, messages.GetArrayLength());
            Assert.IsTrue(messages.EnumerateArray().All(static message =>
                message.TryGetProperty("sequence", out _)
                && message.TryGetProperty("role", out _)
                && !message.TryGetProperty("content", out _)));
            Assert.DoesNotContain(privatePrompt, machine.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(privateReply, machine.Output, StringComparison.Ordinal);

            var missing = RunCommand(fixture, "session", "show", "missing-session", "--json");
            Assert.AreEqual(ExitCodes.InvalidArguments, missing.ExitCode);
            using var missingJson = JsonDocument.Parse(missing.Output);
            Assert.AreEqual("SessionNotFound", missingJson.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [DataRow("--mode", "unsupported")]
    [DataRow("--status", "deleted")]
    [DataRow("--since", "2026-07-24")]
    [DataRow("--limit", "0")]
    [DataRow("--limit", "201")]
    public async Task SessionList_InvalidFilter_ReturnsInvalidArguments(
        string option,
        string value)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateFixtureAsync(root);

            var result = RunCommand(
                fixture,
                "session", "list", option, value, "--json");

            Assert.AreEqual(ExitCodes.InvalidArguments, result.ExitCode, result.AllOutput);
            using var json = JsonDocument.Parse(result.Output);
            Assert.IsFalse(json.RootElement.GetProperty("success").GetBoolean());
            Assert.AreEqual("InvalidSessionFilter", json.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SessionArchiveAndUnarchive_ChangeListVisibilityAndRemainIdempotent()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateFixtureAsync(root);
            var sessionId = RunSession(fixture, "archive this session");

            foreach (var commandName in new[] { "archive", "archive" })
            {
                var archive = RunCommand(
                    fixture,
                    "session", commandName, sessionId, "--json");
                Assert.AreEqual(ExitCodes.Success, archive.ExitCode, archive.AllOutput);
                using var json = JsonDocument.Parse(archive.Output);
                var data = json.RootElement.GetProperty("data");
                Assert.AreEqual(sessionId, data.GetProperty("sessionId").GetString());
                Assert.AreEqual("archived", data.GetProperty("status").GetString());
            }

            var active = RunCommand(fixture, "session", "list", "--json");
            Assert.AreEqual(ExitCodes.Success, active.ExitCode, active.AllOutput);
            using (var json = JsonDocument.Parse(active.Output))
            {
                Assert.AreEqual(
                    0,
                    json.RootElement.GetProperty("data").GetProperty("sessions").GetArrayLength());
            }

            var archived = RunCommand(
                fixture,
                "session", "list", "--status", "archived", "--json");
            Assert.AreEqual(ExitCodes.Success, archived.ExitCode, archived.AllOutput);
            using (var json = JsonDocument.Parse(archived.Output))
            {
                var item = Assert.ContainsSingle(
                    json.RootElement.GetProperty("data").GetProperty("sessions").EnumerateArray().ToArray());
                Assert.AreEqual(sessionId, item.GetProperty("sessionId").GetString());
                Assert.AreEqual("archived", item.GetProperty("status").GetString());
            }

            var show = RunCommand(fixture, "session", "show", sessionId, "--json");
            Assert.AreEqual(ExitCodes.Success, show.ExitCode, show.AllOutput);
            using (var json = JsonDocument.Parse(show.Output))
            {
                Assert.AreEqual(
                    "archived",
                    json.RootElement.GetProperty("data").GetProperty("status").GetString());
            }

            var unarchive = RunCommand(fixture, "session", "unarchive", sessionId);
            Assert.AreEqual(ExitCodes.Success, unarchive.ExitCode, unarchive.AllOutput);
            Assert.Contains(sessionId, unarchive.Output, StringComparison.Ordinal);
            Assert.Contains("active", unarchive.Output, StringComparison.OrdinalIgnoreCase);

            var repeated = RunCommand(
                fixture,
                "session", "unarchive", sessionId, "--json");
            Assert.AreEqual(ExitCodes.Success, repeated.ExitCode, repeated.AllOutput);
            using (var json = JsonDocument.Parse(repeated.Output))
            {
                Assert.AreEqual(
                    "active",
                    json.RootElement.GetProperty("data").GetProperty("status").GetString());
            }

            var restored = RunCommand(fixture, "session", "list", "--json");
            Assert.AreEqual(ExitCodes.Success, restored.ExitCode, restored.AllOutput);
            using (var json = JsonDocument.Parse(restored.Output))
            {
                var item = Assert.ContainsSingle(
                    json.RootElement.GetProperty("data").GetProperty("sessions").EnumerateArray().ToArray());
                Assert.AreEqual(sessionId, item.GetProperty("sessionId").GetString());
                Assert.AreEqual("active", item.GetProperty("status").GetString());
            }
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SessionMaintenanceCommands_WriteDurableAuditAndDryRunDoesNot()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateFixtureAsync(root);
            var sessionId = RunSession(fixture, "audit session maintenance");
            using var audit = new SessionMaintenanceAuditLog(fixture.DataDirectory);

            var archive = RunCommand(
                fixture,
                "session", "archive", sessionId, "--json");
            Assert.AreEqual(ExitCodes.Success, archive.ExitCode, archive.AllOutput);

            var unarchive = RunCommand(
                fixture,
                "session", "unarchive", sessionId, "--json");
            Assert.AreEqual(ExitCodes.Success, unarchive.ExitCode, unarchive.AllOutput);

            var beforeDryRun = await audit.ReadAllAsync(_testContext.CancellationToken);
            var dryRun = RunCommand(
                fixture,
                "session", "compact", sessionId,
                "--strategy", "full",
                "--dry-run",
                "--json");
            Assert.AreEqual(ExitCodes.Success, dryRun.ExitCode, dryRun.AllOutput);
            Assert.HasCount(
                beforeDryRun.Count,
                await audit.ReadAllAsync(_testContext.CancellationToken));

            var compact = RunCommand(
                fixture,
                "session", "compact", sessionId,
                "--strategy", "full",
                "--json");
            Assert.AreEqual(ExitCodes.Success, compact.ExitCode, compact.AllOutput);

            var delete = RunCommand(
                fixture,
                "session", "delete", sessionId,
                "--confirm",
                "--json");
            Assert.AreEqual(ExitCodes.Success, delete.ExitCode, delete.AllOutput);
            Assert.IsTrue(File.Exists(audit.FilePath));

            var records = await audit.ReadAllAsync(_testContext.CancellationToken);
            Assert.HasCount(8, records);
            CollectionAssert.AreEqual(
                MaintenanceAuditPhases,
                records.Select(static record => $"{record.Operation}:{record.Phase}").ToArray());
            Assert.IsTrue(records.All(static record =>
                record.Schema == SessionMaintenanceAuditLog.CurrentSchema));
            foreach (var pair in records.Chunk(2))
            {
                Assert.HasCount(2, pair);
                Assert.AreEqual(pair[0].AuditId, pair[1].AuditId);
                Assert.AreEqual(pair[0].RuntimeInstanceId, pair[1].RuntimeInstanceId);
                Assert.IsNull(pair[0].Outcome);
                Assert.IsNotNull(pair[1].Outcome);
            }
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SessionDelete_RequiresConfirmationAndReportsMissingSession()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateFixtureAsync(root);
            var sessionId = RunSession(fixture, "keep until confirmed");

            var rejected = RunCommand(
                fixture,
                "session", "delete", sessionId, "--json");

            Assert.AreEqual(ExitCodes.InvalidArguments, rejected.ExitCode, rejected.AllOutput);
            using (var json = JsonDocument.Parse(rejected.Output))
            {
                Assert.AreEqual("ConfirmationRequired", json.RootElement
                    .GetProperty("error")
                    .GetProperty("code")
                    .GetString());
            }

            var retained = RunCommand(fixture, "session", "show", sessionId, "--json");
            Assert.AreEqual(ExitCodes.Success, retained.ExitCode, retained.AllOutput);

            var missing = RunCommand(
                fixture,
                "session", "delete", "missing-session", "--confirm", "--json");
            Assert.AreEqual(ExitCodes.InvalidArguments, missing.ExitCode, missing.AllOutput);
            using var missingJson = JsonDocument.Parse(missing.Output);
            Assert.AreEqual("SessionNotFound", missingJson.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SessionDelete_Default_RemovesLiveDataKeepsBlobAndCreatesRecoveryPoint()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateFixtureAsync(root);
            var sessionId = RunSession(fixture, "delete with recovery point");
            var runId = GetLatestRunId(fixture, sessionId);
            var blobId = await AppendBlobReferenceAsync(
                fixture,
                sessionId,
                "blob retained by default");
            var canonicalPath = Path.Combine(
                fixture.DataDirectory,
                "messages",
                sessionId + ".jsonl");
            var compactPath = Path.Combine(
                fixture.DataDirectory,
                "messages",
                sessionId + ".compact.json");
            await File.WriteAllTextAsync(
                compactPath,
                "{\"projection\":true}",
                _testContext.CancellationToken);

            var result = RunCommand(
                fixture,
                "session", "delete", sessionId, "--confirm", "--json");

            Assert.AreEqual(ExitCodes.Success, result.ExitCode, result.AllOutput);
            using (var json = JsonDocument.Parse(result.Output))
            {
                var data = json.RootElement.GetProperty("data");
                Assert.AreEqual(sessionId, data.GetProperty("sessionId").GetString());
                Assert.IsTrue(data.GetProperty("deleted").GetBoolean());
                Assert.IsFalse(data.GetProperty("includeBlobs").GetBoolean());
                Assert.AreEqual(0, data.GetProperty("deletedBlobCount").GetInt32());
            }

            Assert.IsFalse(File.Exists(canonicalPath));
            Assert.IsFalse(File.Exists(compactPath));
            Assert.IsTrue(File.Exists(Path.Combine(
                fixture.DataDirectory,
                "blobs",
                blobId + ".blob")));
            Assert.AreEqual(
                0L,
                await CountSessionRowsAsync(fixture.DataDirectory, sessionId, runId));

            var recoveryRoot = Path.Combine(
                fixture.DataDirectory,
                "recovery",
                "session-deletions");
            var manifest = Assert.ContainsSingle(
                Directory.EnumerateFiles(recoveryRoot, "manifest.json", SearchOption.AllDirectories));
            Assert.Contains(sessionId, await File.ReadAllTextAsync(
                manifest,
                _testContext.CancellationToken), StringComparison.Ordinal);
            var recoveryDirectory = Path.GetDirectoryName(manifest)!;
            Assert.IsTrue(File.Exists(Path.Combine(
                recoveryDirectory,
                "messages",
                sessionId + ".jsonl")));
            Assert.IsTrue(File.Exists(Path.Combine(
                recoveryDirectory,
                "messages",
                sessionId + ".compact.json")));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SessionDelete_IncludeBlobs_RemovesOnlyUnsharedContentAddressedBlob()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateFixtureAsync(root);
            var deletedSessionId = RunSession(fixture, "delete blob owner");
            var retainedSessionId = RunSession(fixture, "retain shared blob owner");
            var deletedRunId = GetLatestRunId(fixture, deletedSessionId);
            var retainedRunId = GetLatestRunId(fixture, retainedSessionId);
            var sharedBlobId = await AppendBlobReferenceAsync(
                fixture,
                deletedSessionId,
                "shared blob content");
            Assert.AreEqual(
                sharedBlobId,
                await AppendBlobReferenceAsync(
                    fixture,
                    retainedSessionId,
                    "shared blob content"));
            var uniqueBlobId = await AppendBlobReferenceAsync(
                fixture,
                deletedSessionId,
                "target-only blob content");
            var uniqueToolBlobId = await StoreBlobAsync(
                fixture,
                "target-only tool blob content");
            await InsertToolBlobReferenceAsync(
                fixture,
                deletedSessionId,
                deletedRunId,
                uniqueToolBlobId);
            var sharedToolBlobId = await StoreBlobAsync(
                fixture,
                "shared tool blob content");
            await InsertToolBlobReferenceAsync(
                fixture,
                deletedSessionId,
                deletedRunId,
                sharedToolBlobId);
            await InsertToolBlobReferenceAsync(
                fixture,
                retainedSessionId,
                retainedRunId,
                sharedToolBlobId);

            var result = RunCommand(
                fixture,
                "session", "delete", deletedSessionId,
                "--confirm", "--include-blobs", "--json");

            Assert.AreEqual(ExitCodes.Success, result.ExitCode, result.AllOutput);
            using (var json = JsonDocument.Parse(result.Output))
            {
                var data = json.RootElement.GetProperty("data");
                Assert.IsTrue(data.GetProperty("includeBlobs").GetBoolean());
                Assert.AreEqual(2, data.GetProperty("deletedBlobCount").GetInt32());
            }

            var blobDirectory = Path.Combine(fixture.DataDirectory, "blobs");
            Assert.IsTrue(File.Exists(Path.Combine(blobDirectory, sharedBlobId + ".blob")));
            Assert.IsFalse(File.Exists(Path.Combine(blobDirectory, uniqueBlobId + ".blob")));
            Assert.IsFalse(File.Exists(Path.Combine(blobDirectory, uniqueToolBlobId + ".blob")));
            Assert.IsTrue(File.Exists(Path.Combine(blobDirectory, sharedToolBlobId + ".blob")));
            var retained = RunCommand(
                fixture,
                "session", "show", retainedSessionId, "--messages", "--json");
            Assert.AreEqual(ExitCodes.Success, retained.ExitCode, retained.AllOutput);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SessionDelete_WhenWorkspaceIsLocked_ReturnsStableWorkspaceError()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateFixtureAsync(root);
            var sessionId = RunSession(fixture, "locked session");
            await using var runtime = await LocalRuntime.StartAsync(new LocalRuntimeOptions(
                fixture.Workspace,
                fixture.DataDirectory,
                Path.Combine(fixture.DataDirectory, "logs"),
                "session-delete-lock-holder",
                static _ => throw new InvalidOperationException("Provider resolution is unavailable."))
            {
                MemoryUserHome = fixture.Root
            }, _testContext.CancellationToken);

            var result = RunCommand(
                fixture,
                "session", "delete", sessionId, "--confirm", "--json");

            Assert.AreEqual(ExitCodes.WorkspaceError, result.ExitCode, result.AllOutput);
            using var json = JsonDocument.Parse(result.Output);
            Assert.AreEqual("SessionDeleteFailed", json.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [DataRow("archive")]
    [DataRow("unarchive")]
    public async Task SessionArchive_MissingSession_ReturnsStableError(string commandName)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateFixtureAsync(root);

            var result = RunCommand(
                fixture,
                "session", commandName, "missing-session", "--json");

            Assert.AreEqual(ExitCodes.InvalidArguments, result.ExitCode, result.AllOutput);
            using var json = JsonDocument.Parse(result.Output);
            Assert.AreEqual("SessionNotFound", json.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SessionCompact_DryRun_UsesDefaultAndOverrideSelectionWithoutWritingCache()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateFixtureAsync(root);
            var sessionId = RunSession(fixture, "compact dry run");
            fixture.ProviderState.ResetSummaryCalls();
            var cachePath = GetCompactCachePath(fixture, sessionId);

            var defaults = RunCommand(
                fixture,
                "session", "compact", sessionId, "--dry-run", "--json");

            Assert.AreEqual(ExitCodes.Success, defaults.ExitCode, defaults.AllOutput);
            using (var json = JsonDocument.Parse(defaults.Output))
            {
                var data = json.RootElement.GetProperty("data");
                Assert.AreEqual("summary", data.GetProperty("strategy").GetString());
                Assert.AreEqual(ProviderId, data.GetProperty("providerId").GetString());
                Assert.AreEqual(ModelId, data.GetProperty("modelId").GetString());
                Assert.IsTrue(data.GetProperty("dryRun").GetBoolean());
                Assert.IsFalse(data.GetProperty("cacheHit").GetBoolean());
                Assert.IsFalse(data.GetProperty("cacheWritten").GetBoolean());
                Assert.AreEqual(
                    JsonValueKind.Null,
                    data.GetProperty("afterEstimatedTokens").ValueKind);
            }

            var overridden = RunCommand(
                fixture,
                "session", "compact", sessionId,
                "--provider", ProviderId,
                "--model", "override-model",
                "--dry-run",
                "--json");

            Assert.AreEqual(ExitCodes.Success, overridden.ExitCode, overridden.AllOutput);
            using (var json = JsonDocument.Parse(overridden.Output))
            {
                var data = json.RootElement.GetProperty("data");
                Assert.AreEqual(ProviderId, data.GetProperty("providerId").GetString());
                Assert.AreEqual("override-model", data.GetProperty("modelId").GetString());
            }

            Assert.AreEqual(0, fixture.ProviderState.SummaryCallCount);
            Assert.IsFalse(File.Exists(cachePath));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SessionCompact_Summary_ReusesCacheAndForceOrHistoryChangeRegenerates()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateFixtureAsync(root);
            var sessionId = RunSession(fixture, "compact summary source");
            fixture.ProviderState.ResetSummaryCalls();
            var canonicalPath = Path.Combine(
                fixture.DataDirectory,
                "messages",
                sessionId + ".jsonl");
            var cachePath = GetCompactCachePath(fixture, sessionId);
            var canonicalBefore = await File.ReadAllBytesAsync(
                canonicalPath,
                _testContext.CancellationToken);

            var generated = RunCommand(
                fixture,
                "session", "compact", sessionId, "--json");

            AssertCompactionResult(generated, "summary", cacheHit: false, cacheWritten: true);
            Assert.AreEqual(1, fixture.ProviderState.SummaryCallCount);
            Assert.IsTrue(File.Exists(cachePath));
            CollectionAssert.AreEqual(
                canonicalBefore,
                await File.ReadAllBytesAsync(canonicalPath, _testContext.CancellationToken));
            var summaryRequest = fixture.ProviderState.LastSummaryRequest;
            Assert.IsNotNull(summaryRequest);
            Assert.IsNull(summaryRequest.Tools);

            var cached = RunCommand(
                fixture,
                "session", "compact", sessionId, "--json");

            AssertCompactionResult(cached, "summary", cacheHit: true, cacheWritten: false);
            Assert.AreEqual(1, fixture.ProviderState.SummaryCallCount);

            var forced = RunCommand(
                fixture,
                "session", "compact", sessionId, "--force", "--json");

            AssertCompactionResult(forced, "summary", cacheHit: false, cacheWritten: true);
            Assert.AreEqual(2, fixture.ProviderState.SummaryCallCount);

            var store = new ConversationStore(fixture.DataDirectory);
            await store.AppendMessageAsync(
                sessionId,
                "Expert",
                CreateTextDraft("history changed after cache creation"),
                _testContext.CancellationToken);
            var changedCanonical = await File.ReadAllBytesAsync(
                canonicalPath,
                _testContext.CancellationToken);

            var regenerated = RunCommand(
                fixture,
                "session", "compact", sessionId, "--json");

            AssertCompactionResult(regenerated, "summary", cacheHit: false, cacheWritten: true);
            Assert.AreEqual(3, fixture.ProviderState.SummaryCallCount);
            CollectionAssert.AreEqual(
                changedCanonical,
                await File.ReadAllBytesAsync(canonicalPath, _testContext.CancellationToken));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SessionCompact_SummaryProviderFailure_PreservesExistingValidCache()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateFixtureAsync(root);
            var sessionId = RunSession(fixture, "compact provider failure");
            fixture.ProviderState.ResetSummaryCalls();

            var generated = RunCommand(
                fixture,
                "session", "compact", sessionId, "--json");
            AssertCompactionResult(generated, "summary", cacheHit: false, cacheWritten: true);

            var cachePath = GetCompactCachePath(fixture, sessionId);
            var cacheBefore = await File.ReadAllBytesAsync(
                cachePath,
                _testContext.CancellationToken);
            fixture.ProviderState.FailSummaryRequests = true;

            var failed = RunCommand(
                fixture,
                "session", "compact", sessionId, "--force", "--json");

            Assert.AreEqual(ExitCodes.WorkspaceError, failed.ExitCode, failed.AllOutput);
            using (var json = JsonDocument.Parse(failed.Output))
            {
                Assert.AreEqual("SessionCompactionFailed", json.RootElement
                    .GetProperty("error")
                    .GetProperty("code")
                    .GetString());
            }

            Assert.AreEqual(2, fixture.ProviderState.SummaryCallCount);
            CollectionAssert.AreEqual(
                cacheBefore,
                await File.ReadAllBytesAsync(cachePath, _testContext.CancellationToken));
            using var cache = JsonDocument.Parse(cacheBefore);
            Assert.AreEqual(
                ConversationCompactCacheV1.CurrentSchema,
                cache.RootElement.GetProperty("schema").GetString());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SessionCompact_FullAndSlidingWindow_PreserveToolRoundAtomicity()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateFixtureAsync(root);
            var sessionId = RunSession(fixture, "old compact context");
            var store = new ConversationStore(fixture.DataDirectory);
            await store.AppendMessageAsync(
                sessionId,
                "Expert",
                CreateToolCallDraft("compact-call"),
                _testContext.CancellationToken);
            await store.AppendMessageAsync(
                sessionId,
                "Expert",
                CreateToolResultDraft("compact-call"),
                _testContext.CancellationToken);
            var canonicalPath = Path.Combine(
                fixture.DataDirectory,
                "messages",
                sessionId + ".jsonl");
            var canonicalBefore = await File.ReadAllBytesAsync(
                canonicalPath,
                _testContext.CancellationToken);

            var full = RunCommand(
                fixture,
                "session", "compact", sessionId,
                "--strategy", "full",
                "--json");

            AssertCompactionResult(full, "full", cacheHit: false, cacheWritten: true);
            using (var json = JsonDocument.Parse(full.Output))
            {
                var data = json.RootElement.GetProperty("data");
                Assert.AreEqual(
                    data.GetProperty("sourceMessageCount").GetInt32(),
                    data.GetProperty("projectedMessageCount").GetInt32());
                Assert.AreEqual(0, data.GetProperty("droppedMessageCount").GetInt32());
            }

            var sliding = RunCommand(
                fixture,
                "session", "compact", sessionId,
                "--strategy", "sliding-window",
                "--keep-last-tokens", "2",
                "--json");

            AssertCompactionResult(
                sliding,
                "sliding-window",
                cacheHit: false,
                cacheWritten: true);
            using (var json = JsonDocument.Parse(sliding.Output))
            {
                var data = json.RootElement.GetProperty("data");
                Assert.AreEqual(2, data.GetProperty("projectedMessageCount").GetInt32());
                Assert.IsGreaterThanOrEqualTo(
                    2,
                    data.GetProperty("droppedMessageCount").GetInt32());
            }

            using var cache = JsonDocument.Parse(await File.ReadAllTextAsync(
                GetCompactCachePath(fixture, sessionId),
                _testContext.CancellationToken));
            var messages = cache.RootElement.GetProperty("projectionMessages");
            Assert.AreEqual(2, messages.GetArrayLength());
            Assert.AreEqual("assistant", messages[0].GetProperty("role").GetString());
            Assert.AreEqual("tool", messages[1].GetProperty("role").GetString());
            Assert.AreEqual("compact-call", messages[0]
                .GetProperty("content")[0]
                .GetProperty("callId")
                .GetString());
            Assert.AreEqual("compact-call", messages[1]
                .GetProperty("content")[0]
                .GetProperty("callId")
                .GetString());
            CollectionAssert.AreEqual(
                canonicalBefore,
                await File.ReadAllBytesAsync(canonicalPath, _testContext.CancellationToken));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [DataRow("--strategy", "unsupported")]
    [DataRow("--keep-last-tokens", "0")]
    public async Task SessionCompact_InvalidOption_ReturnsInvalidArguments(
        string option,
        string value)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateFixtureAsync(root);
            var sessionId = RunSession(fixture, "invalid compact option");

            var result = RunCommand(
                fixture,
                "session", "compact", sessionId,
                option, value,
                "--json");

            Assert.AreEqual(ExitCodes.InvalidArguments, result.ExitCode, result.AllOutput);
            using var json = JsonDocument.Parse(result.Output);
            Assert.AreEqual("InvalidCompactionOptions", json.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SessionCompact_MissingSessionAndWorkspaceLock_ReturnStableErrors()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var fixture = await CreateFixtureAsync(root);
            var missing = RunCommand(
                fixture,
                "session", "compact", "missing-session", "--dry-run", "--json");

            Assert.AreEqual(ExitCodes.InvalidArguments, missing.ExitCode, missing.AllOutput);
            using (var json = JsonDocument.Parse(missing.Output))
            {
                Assert.AreEqual("SessionNotFound", json.RootElement
                    .GetProperty("error")
                    .GetProperty("code")
                    .GetString());
            }

            var sessionId = RunSession(fixture, "locked compact session");
            await using var runtime = await LocalRuntime.StartAsync(new LocalRuntimeOptions(
                fixture.Workspace,
                fixture.DataDirectory,
                Path.Combine(fixture.DataDirectory, "logs"),
                "session-compact-lock-holder",
                static _ => throw new InvalidOperationException("Provider resolution is unavailable."))
            {
                MemoryUserHome = fixture.Root
            }, _testContext.CancellationToken);

            var locked = RunCommand(
                fixture,
                "session", "compact", sessionId, "--dry-run", "--json");

            Assert.AreEqual(ExitCodes.WorkspaceError, locked.ExitCode, locked.AllOutput);
            using var lockedJson = JsonDocument.Parse(locked.Output);
            Assert.AreEqual("SessionCompactionFailed", lockedJson.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private async Task<SessionFixture> CreateFixtureAsync(
        string root,
        string providerReply = "session reply")
    {
        var configDirectory = Path.Combine(root, "config");
        var workspace = Path.Combine(root, "workspace");
        var dataDirectory = Path.Combine(root, "data");
        Directory.CreateDirectory(workspace);

        var loader = new StandaloneConfigLoader(configDirectory);
        await loader.SaveAsync(new StandaloneConfig
        {
            DefaultProvider = ProviderId,
            DefaultModel = ModelId,
            DefaultAgent = AgentId,
            Providers =
            [
                new ProviderEntry
                {
                    Name = ProviderId,
                    Protocol = "OpenAI",
                    BaseUrl = "https://example.invalid/v1",
                    ApiKey = "session-test-key",
                    Models = [ModelId]
                }
            ]
        }, _testContext.CancellationToken);
        await loader.SaveAgentAsync(new AgentConfig
        {
            Id = AgentId,
            Name = "Session Agent",
            SystemPrompt = "Answer deterministically.",
            Provider = ProviderId,
            Model = ModelId
        }, _testContext.CancellationToken);

        return new SessionFixture(
            root,
            configDirectory,
            workspace,
            dataDirectory,
            providerReply,
            new CompactionProviderState());
    }

    private static string RunSession(SessionFixture fixture, string prompt)
    {
        var result = RunCommand(
            fixture,
            "run",
            "--input", prompt,
            "--no-stream",
            "--json");
        Assert.AreEqual(ExitCodes.Success, result.ExitCode, result.AllOutput);
        using var json = JsonDocument.Parse(result.Output);
        return json.RootElement
            .GetProperty("data")
            .GetProperty("sessionId")
            .GetString()
            ?? throw new AssertFailedException("The run result did not contain a Session id.");
    }

    private static string GetLatestRunId(SessionFixture fixture, string sessionId)
    {
        var result = RunCommand(fixture, "session", "show", sessionId, "--json");
        Assert.AreEqual(ExitCodes.Success, result.ExitCode, result.AllOutput);
        using var json = JsonDocument.Parse(result.Output);
        return json.RootElement
            .GetProperty("data")
            .GetProperty("latestRun")
            .GetProperty("runId")
            .GetString()
            ?? throw new AssertFailedException("The Session did not contain a latest Run id.");
    }

    private static string GetCompactCachePath(SessionFixture fixture, string sessionId) =>
        Path.Combine(fixture.DataDirectory, "messages", sessionId + ".compact.json");

    private static ConversationMessageDraft CreateTextDraft(string text)
    {
        var encodedText = JsonEncodedText.Encode(text).ToString();
        using var content = JsonDocument.Parse(
            $"[{{\"type\":\"text\",\"text\":\"{encodedText}\"}}]");
        return new ConversationMessageDraft(
            "compact-text-" + Guid.NewGuid().ToString("N"),
            AgentId,
            "user",
            content.RootElement.Clone(),
            DateTimeOffset.UtcNow);
    }

    private static ConversationMessageDraft CreateToolCallDraft(string callId)
    {
        using var content = JsonDocument.Parse($$"""
            [
              {
                "type": "tool_call",
                "callId": "{{callId}}",
                "toolId": "test.tool",
                "name": "test_tool",
                "arguments": {}
              }
            ]
            """);
        return new ConversationMessageDraft(
            "compact-tool-call-" + Guid.NewGuid().ToString("N"),
            AgentId,
            "assistant",
            content.RootElement.Clone(),
            DateTimeOffset.UtcNow);
    }

    private static ConversationMessageDraft CreateToolResultDraft(string callId)
    {
        using var content = JsonDocument.Parse($$"""
            [
              {
                "type": "tool_result",
                "callId": "{{callId}}",
                "toolId": "test.tool",
                "success": true,
                "content": [
                  {
                    "type": "text",
                    "text": "tool result"
                  }
                ]
              }
            ]
            """);
        return new ConversationMessageDraft(
            "compact-tool-result-" + Guid.NewGuid().ToString("N"),
            AgentId,
            "tool",
            content.RootElement.Clone(),
            DateTimeOffset.UtcNow);
    }

    private static void AssertCompactionResult(
        CliResult result,
        string strategy,
        bool cacheHit,
        bool cacheWritten)
    {
        Assert.AreEqual(ExitCodes.Success, result.ExitCode, result.AllOutput);
        using var json = JsonDocument.Parse(result.Output);
        var data = json.RootElement.GetProperty("data");
        Assert.AreEqual(strategy, data.GetProperty("strategy").GetString());
        Assert.AreEqual(cacheHit, data.GetProperty("cacheHit").GetBoolean());
        Assert.AreEqual(cacheWritten, data.GetProperty("cacheWritten").GetBoolean());
    }

    private async Task<string> AppendBlobReferenceAsync(
        SessionFixture fixture,
        string sessionId,
        string content)
    {
        var store = new ConversationStore(fixture.DataDirectory);
        var reference = await store.BlobStore.StoreAsync(
            Encoding.UTF8.GetBytes(content),
            "text/plain; charset=utf-8",
            "conversation",
            DateTimeOffset.UtcNow.AddDays(365),
            _testContext.CancellationToken);
        using var contentJson = JsonDocument.Parse($$"""
            [
              {
                "type": "blob_ref",
                "blob": {
                  "blobId": "{{reference.BlobId}}",
                  "length": {{reference.Length}},
                  "sha256": "{{reference.Sha256}}",
                  "contentType": "{{reference.ContentType}}",
                  "accessScope": "{{reference.AccessScope}}",
                  "expiresAt": "{{reference.ExpiresAt:O}}"
                }
              }
            ]
            """);
        await store.AppendMessageAsync(
            sessionId,
            "Expert",
            new ConversationMessageDraft(
                "blob-delete-" + Guid.NewGuid().ToString("N"),
                AgentId,
                "assistant",
                contentJson.RootElement.Clone(),
                DateTimeOffset.UtcNow),
            _testContext.CancellationToken);
        return reference.BlobId;
    }

    private async Task<string> StoreBlobAsync(SessionFixture fixture, string content)
    {
        var store = new ConversationBlobStore(fixture.DataDirectory);
        var reference = await store.StoreAsync(
            Encoding.UTF8.GetBytes(content),
            "text/plain; charset=utf-8",
            "tool-result",
            DateTimeOffset.UtcNow.AddDays(365),
            _testContext.CancellationToken);
        return reference.BlobId;
    }

    private async Task InsertToolBlobReferenceAsync(
        SessionFixture fixture,
        string sessionId,
        string runId,
        string blobId)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fixture.DataDirectory, "state.db"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(_testContext.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tool_intents(call_id, run_id, session_id, result_blob_id)
            VALUES($callId, $runId, $sessionId, $blobId);
            """;
        command.Parameters.AddWithValue("$callId", "delete-call-" + Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$blobId", blobId);
        await command.ExecuteNonQueryAsync(_testContext.CancellationToken);
    }

    private async Task<long> CountSessionRowsAsync(
        string dataDirectory,
        string sessionId,
        string runId)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "state.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(_testContext.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM sessions WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM runs WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM session_idempotency WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM run_idempotency WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM event_outbox WHERE run_id = $runId)
              + (SELECT COUNT(*) FROM message_index WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM session_selections WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM agent_snapshots WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM tool_intents WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM tool_grants WHERE run_id = $runId)
              + (SELECT COUNT(*) FROM tool_approvals WHERE run_id = $runId)
              + (SELECT COUNT(*) FROM tool_audit WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM invocation_snapshots WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM meeting_sessions WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM meeting_rounds WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM meeting_participants WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM meeting_invocations WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM work_sessions WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM work_plan_revisions WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM work_steps WHERE session_id = $sessionId)
              + (SELECT COUNT(*) FROM work_background_jobs WHERE session_id = $sessionId);
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$runId", runId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(_testContext.CancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static CliResult RunCommand(SessionFixture fixture, params string[] commandArguments)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var error = new StringWriter(CultureInfo.InvariantCulture);
        string[] args =
        [
            .. commandArguments,
            "--workspace", fixture.Workspace,
            "--data-dir", fixture.DataDirectory
        ];

        var exitCode = CliApplication.RunForTests(
            args,
            output,
            new StringReader(string.Empty),
            fixture.ConfigDirectory,
            _ => _ => new DeterministicProvider(
                fixture.ProviderReply,
                fixture.ProviderState),
            memoryUserHome: fixture.Root,
            error: error);
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"madorin-session-cli-stage7-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record SessionFixture(
        string Root,
        string ConfigDirectory,
        string Workspace,
        string DataDirectory,
        string ProviderReply,
        CompactionProviderState ProviderState);

    private sealed record CliResult(int ExitCode, string Output, string Error)
    {
        public string AllOutput => Output + Error;
    }

    private sealed class DeterministicProvider(
        string response,
        CompactionProviderState state) : IRuntimeProviderAdapter
    {
        private readonly string _modelId = StandaloneSessionCommandsStage7Tests.ModelId;

        public string ProviderId => StandaloneSessionCommandsStage7Tests.ProviderId;

        public string ModelId => _modelId;

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new(StandaloneSessionCommandsStage7Tests.ModelId,
                StandaloneSessionCommandsStage7Tests.ModelId,
                ContextWindow: 8192),
             new("override-model", "override-model", ContextWindow: 4096)];

        public ValueTask<ProviderTokenEstimate> EstimateTokensAsync(
            RuntimeProviderRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ProviderTokenEstimate(
                request.Messages.Length,
                null,
                ProviderUsageAccuracy.Estimated));
        }

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            var effectiveResponse = response;
            if (request.AgentId == "session-compactor")
            {
                Interlocked.Increment(ref state.SummaryCallCount);
                state.LastSummaryRequest = request;
                if (state.FailSummaryRequests)
                {
                    throw new InvalidOperationException("Injected summary Provider failure.");
                }

                effectiveResponse = state.SummaryReply;
            }

            yield return new TextDeltaProviderEvent(request.InvocationId, effectiveResponse);
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class CompactionProviderState
    {
        public int SummaryCallCount;

        public string SummaryReply { get; } = "deterministic compact summary";

        public RuntimeProviderRequest? LastSummaryRequest { get; set; }

        public bool FailSummaryRequests { get; set; }

        public void ResetSummaryCalls()
        {
            SummaryCallCount = 0;
            LastSummaryRequest = null;
            FailSummaryRequests = false;
        }
    }
}
