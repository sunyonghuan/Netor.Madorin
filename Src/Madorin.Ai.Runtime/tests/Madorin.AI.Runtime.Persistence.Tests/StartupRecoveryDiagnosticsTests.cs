using System.Text.Json;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Tests;

[TestClass]
public sealed class StartupRecoveryDiagnosticsTests
{
    private readonly TestContext _testContext;

    public StartupRecoveryDiagnosticsTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    public async Task InitializeAsync_AfterInterruptedRun_WritesWalAndRetentionDiagnostics()
    {
        var dataDirectory = CreateDataDirectory();
        try
        {
            string runId;
            await using (var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                _testContext.CancellationToken))
            {
                var repository = new SqliteSessionRepository(connection);
                var sessionId = await repository.CreateSessionAsync(
                    RuntimeMode.Expert,
                    "startup-diagnostic-session",
                    TimeSpan.FromDays(1),
                    _testContext.CancellationToken);
                runId = await repository.CreateRunAsync(
                    sessionId,
                    "startup-diagnostic-run",
                    TimeSpan.FromDays(1),
                    _testContext.CancellationToken);
                await repository.TransitionRunStatusAsync(
                    runId,
                    RunStatus.Accepted,
                    RunStatus.Preparing,
                    _testContext.CancellationToken);
                await repository.TransitionRunStatusAsync(
                    runId,
                    RunStatus.Preparing,
                    RunStatus.Running,
                    _testContext.CancellationToken);

                await using var intent = connection.CreateCommand();
                intent.CommandText = """
                    INSERT INTO tool_intents(
                        call_id, invocation_id, run_id, session_id, tool_id,
                        arguments_hash, status, created_at)
                    VALUES(
                        'startup-call', 'startup-invocation', $runId, $sessionId,
                        'host.process.exec', 'arguments-hash', 'sent',
                        '2026-07-24T00:00:00.000Z');
                    """;
                intent.Parameters.AddWithValue("$runId", runId);
                intent.Parameters.AddWithValue("$sessionId", sessionId);
                await intent.ExecuteNonQueryAsync(_testContext.CancellationToken);
            }

            await using (var recovered = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                _testContext.CancellationToken))
            {
                var repository = new SqliteSessionRepository(recovered);
                Assert.AreEqual(
                    RunStatus.Interrupted,
                    await repository.GetRunStatusAsync(runId, _testContext.CancellationToken));
            }

            var reportPath = Path.Combine(dataDirectory, "logs", "startup-recovery.json");
            Assert.IsTrue(File.Exists(reportPath));
            await using var reportStream = File.OpenRead(reportPath);
            using var report = await JsonDocument.ParseAsync(
                reportStream,
                cancellationToken: _testContext.CancellationToken);
            var root = report.RootElement;
            Assert.AreEqual("ok", root.GetProperty("quickCheck").GetString());
            Assert.AreEqual(SqliteSchema.CurrentVersion, root.GetProperty("schemaVersion").GetInt32());
            Assert.AreEqual(0, root.GetProperty("permanentRunningCount").GetInt32());
            Assert.AreEqual(1, root.GetProperty("recoverableToolIntentCount").GetInt32());
            Assert.AreEqual(6, root.GetProperty("retainedDirectories").GetArrayLength());
            Assert.IsGreaterThanOrEqualTo(
                0,
                root.GetProperty("walCheckpoint").GetProperty("checkpointedFrames").GetInt32());
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task InitializeAsync_WithFutureSchema_ReturnsExplicitCompatibilityDiagnostic()
    {
        var dataDirectory = CreateDataDirectory();
        try
        {
            await using (var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                _testContext.CancellationToken))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO schema_versions(version, applied_at)
                    VALUES(999, '2026-07-24T00:00:00.000Z');
                    """;
                await command.ExecuteNonQueryAsync(_testContext.CancellationToken);
            }

            var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(
                async () => await DataDirectoryInitializer.InitializeAsync(
                    dataDirectory,
                    _testContext.CancellationToken));
            StringAssert.Contains(exception.Message, "newer than supported version");
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task InitializeAsync_WithCorruptDatabase_ReturnsExplicitIntegrityDiagnostic()
    {
        var dataDirectory = CreateDataDirectory();
        try
        {
            Directory.CreateDirectory(dataDirectory);
            await File.WriteAllBytesAsync(
                Path.Combine(dataDirectory, "state.db"),
                "not-a-sqlite-database"u8.ToArray(),
                _testContext.CancellationToken);

            var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(
                async () => await DataDirectoryInitializer.InitializeAsync(
                    dataDirectory,
                    _testContext.CancellationToken));
            StringAssert.Contains(exception.Message, "SQLite startup recovery failed");
            Assert.IsInstanceOfType<SqliteException>(exception.InnerException);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    private string CreateDataDirectory() =>
        Path.Combine(
            _testContext.TestRunDirectory ?? Path.GetTempPath(),
            "startup-recovery-" + Guid.NewGuid().ToString("N"));
}
