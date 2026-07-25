using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Madorin.AI.Runtime.Cli;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
[DoNotParallelize]
public sealed class StandaloneDatabaseCommandsStage7Tests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public void DatabaseCommand_SeparatesReadOnlyCheckFromRepair()
    {
        var root = CliApplication.CreateRootCommand(
            new StringWriter(),
            new StringReader(string.Empty));
        var database = root.Subcommands.Single(static command => command.Name == "db");
        var check = database.Subcommands.Single(static command => command.Name == "check");

        Assert.DoesNotContain("--repair", check.Options.Select(static option => option.Name));
        Assert.DoesNotContain("--yes", check.Options.Select(static option => option.Name));
        Assert.Contains("--verbose", check.Options.Select(static option => option.Name));
        Assert.Contains("repair", database.Subcommands.Select(static command => command.Name));

        var rebuild = database.Subcommands.Single(static command => command.Name == "rebuild");
        Assert.Contains("--dry-run", rebuild.Options.Select(static option => option.Name));
        Assert.Contains("--confirm", rebuild.Options.Select(static option => option.Name));

        var migrate = database.Subcommands.Single(static command => command.Name == "migrate");
        Assert.Contains("--to", migrate.Options.Select(static option => option.Name));
        Assert.Contains("--dry-run", migrate.Options.Select(static option => option.Name));
        Assert.DoesNotContain("--target", migrate.Options.Select(static option => option.Name));
    }

    [TestMethod]
    public async Task DbCheck_HealthyData_DoesNotWriteAnything()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "check", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("ok", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            Assert.IsTrue(document.RootElement.GetProperty("data").GetProperty("healthy").GetBoolean());
            Assert.AreEqual(0, document.RootElement.GetProperty("data").GetProperty("issueCount").GetInt32());
            Assert.AreEqual(SqliteSchema.CurrentVersion, document.RootElement.GetProperty("data").GetProperty("schemaVersion").GetInt32());
            Assert.AreEqual("wal", document.RootElement.GetProperty("data").GetProperty("journalMode").GetString());
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbCheck_DamagedTail_ReportsRepairableIssueWithoutWriting()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await File.AppendAllTextAsync(
                fixture.MessagePath,
                "{\"incomplete\":",
                Encoding.UTF8,
                TestContext.CancellationToken);
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "check", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.GeneralError, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("issues", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            var issue = document.RootElement.GetProperty("data").GetProperty("issues")
                .EnumerateArray()
                .Single(item => item.GetProperty("code").GetString() == "jsonl_tail_damage");
            Assert.IsTrue(issue.GetProperty("repairable").GetBoolean());
            Assert.AreEqual(fixture.SessionId, issue.GetProperty("sessionId").GetString());
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbCheck_SchemaWalAndSessionMismatch_ReportsStableDiagnosticsWithoutRepair()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await ExecuteAsync(
                fixture.DataDirectory,
                """
                PRAGMA wal_checkpoint(TRUNCATE);
                PRAGMA journal_mode = DELETE;
                DROP INDEX idx_sessions_status;
                PRAGMA foreign_keys = OFF;
                DELETE FROM sessions WHERE session_id = $sessionId;
                """,
                fixture.SessionId);
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "check", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.GeneralError, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            var codes = document.RootElement.GetProperty("data").GetProperty("issues")
                .EnumerateArray()
                .Select(static item => item.GetProperty("code").GetString())
                .ToArray();
            Assert.Contains("journal_mode_not_wal", codes);
            Assert.Contains("schema_incomplete", codes);
            Assert.Contains("message_file_without_session", codes);
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbCheck_ActiveWal_SeesCommittedWalDataWithoutWritingSourceFiles()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var writerConnection = new SqliteConnection(
                $"Data Source={Path.Combine(fixture.DataDirectory, "state.db")};Pooling=False");
            await writerConnection.OpenAsync(TestContext.CancellationToken);
            await using (var command = writerConnection.CreateCommand())
            {
                command.CommandText = """
                    PRAGMA wal_autocheckpoint = 0;
                    INSERT INTO sessions(session_id, mode, status, created_at, updated_at)
                    VALUES('wal-only-session', 'Expert', 'Active', $now, $now);
                    """;
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
            }

            Assert.IsTrue(File.Exists(Path.Combine(fixture.DataDirectory, "state.db-wal")));
            Assert.IsTrue(File.Exists(Path.Combine(fixture.DataDirectory, "state.db-shm")));
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "check", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.GeneralError, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            var issue = document.RootElement.GetProperty("data").GetProperty("issues")
                .EnumerateArray()
                .Single(item => item.GetProperty("code").GetString() == "session_without_message_file");
            Assert.AreEqual("wal-only-session", issue.GetProperty("sessionId").GetString());
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbCheck_WalWithoutSharedMemory_SeesCommittedWalDataWithoutWritingSourceFiles()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var writerConnection = new SqliteConnection(
                $"Data Source={Path.Combine(fixture.DataDirectory, "state.db")};Pooling=False");
            await writerConnection.OpenAsync(TestContext.CancellationToken);
            await using (var command = writerConnection.CreateCommand())
            {
                command.CommandText = """
                    PRAGMA wal_autocheckpoint = 0;
                    INSERT INTO sessions(session_id, mode, status, created_at, updated_at)
                    VALUES('wal-without-shm-session', 'Expert', 'Active', $now, $now);
                    """;
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
            }

            var copiedDataDirectory = Path.Combine(fixture.Root, "wal-without-shm");
            var copiedMessagesDirectory = Path.Combine(copiedDataDirectory, "messages");
            Directory.CreateDirectory(copiedMessagesDirectory);
            await CopySharedFileAsync(
                Path.Combine(fixture.DataDirectory, "state.db"),
                Path.Combine(copiedDataDirectory, "state.db"));
            await CopySharedFileAsync(
                Path.Combine(fixture.DataDirectory, "state.db-wal"),
                Path.Combine(copiedDataDirectory, "state.db-wal"));
            await CopySharedFileAsync(
                fixture.MessagePath,
                Path.Combine(copiedMessagesDirectory, Path.GetFileName(fixture.MessagePath)));
            Assert.IsFalse(File.Exists(Path.Combine(copiedDataDirectory, "state.db-shm")));
            var before = await CaptureTreeAsync(copiedDataDirectory);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "check", "--workspace", fixture.Workspace, "--data-dir", copiedDataDirectory, "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.GeneralError, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            var issue = document.RootElement.GetProperty("data").GetProperty("issues")
                .EnumerateArray()
                .Single(item => item.GetProperty("code").GetString() == "session_without_message_file");
            Assert.AreEqual("wal-without-shm-session", issue.GetProperty("sessionId").GetString());
            AssertTreeEqual(before, await CaptureTreeAsync(copiedDataDirectory));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbRepair_DeclinedConfirmation_PreservesOriginalData()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await AppendDamagedTailAsync(fixture.MessagePath);
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "repair", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory],
                output,
                new StringReader("n" + Environment.NewLine),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.UserInterrupted, exitCode, output.ToString());
            Assert.Contains("No changes were made", output.ToString(), StringComparison.Ordinal);
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbRepair_DryRun_ReturnsPlanWithoutLockBackupOrWrites()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await AppendDamagedTailAsync(fixture.MessagePath);
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "repair", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--dry-run", "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("dry-run", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            Assert.IsGreaterThan(
                0,
                document.RootElement.GetProperty("data").GetProperty("repairableIssueCount").GetInt32());
            Assert.AreEqual(JsonValueKind.Null, document.RootElement.GetProperty("data").GetProperty("backupPath").ValueKind);
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbRepair_JsonWithoutYes_ReturnsInvalidArgumentsWithoutWriting()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await AppendDamagedTailAsync(fixture.MessagePath);
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "repair", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.InvalidArguments, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("error", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            var message = document.RootElement.GetProperty("data").GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains(
                "--yes is required",
                message,
                StringComparison.Ordinal);
            Assert.AreEqual(JsonValueKind.Null, document.RootElement.GetProperty("data").GetProperty("backupPath").ValueKind);
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbRepair_Confirmed_BacksUpAndRepairsTailAndMessageIndex()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await AppendDamagedTailAsync(fixture.MessagePath);
            await ExecuteAsync(
                fixture.DataDirectory,
                "DELETE FROM message_index WHERE session_id = $sessionId;",
                fixture.SessionId);
            var damagedBytes = await File.ReadAllBytesAsync(
                fixture.MessagePath,
                TestContext.CancellationToken);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "repair", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--yes", "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("ok", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            var backupPath = document.RootElement.GetProperty("data").GetProperty("backupPath").GetString();
            Assert.IsNotNull(backupPath);
            Assert.IsTrue(File.Exists(Path.Combine(backupPath, "state.db")));
            var backupMessagePath = Path.Combine(backupPath, "messages", fixture.SessionId + ".jsonl");
            Assert.IsTrue(File.Exists(backupMessagePath));
            CollectionAssert.AreEqual(
                damagedBytes,
                await File.ReadAllBytesAsync(backupMessagePath, TestContext.CancellationToken));
            Assert.AreEqual(
                1L,
                await ExecuteScalarAsync(
                    fixture.DataDirectory,
                    "SELECT COUNT(*) FROM message_index WHERE session_id = $sessionId;",
                    fixture.SessionId));

            var checkOutput = new StringWriter();
            var checkExitCode = Run(
                ["db", "check", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--json"],
                checkOutput,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);
            Assert.AreEqual(ExitCodes.Success, checkExitCode, checkOutput.ToString());
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbRepair_WorkspaceLockConflict_PreservesOriginalData()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await AppendDamagedTailAsync(fixture.MessagePath);
            await using var workspaceLock = await WorkspaceWriteLock.AcquireAsync(
                fixture.Workspace,
                "test-lock-holder",
                ct: TestContext.CancellationToken);
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "repair", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--yes", "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.WorkspaceError, exitCode, output.ToString());
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbRepair_UnrecoverableMiddleLine_PreservesOriginalData()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var lines = await File.ReadAllLinesAsync(
                fixture.MessagePath,
                TestContext.CancellationToken);
            await File.WriteAllLinesAsync(
                fixture.MessagePath,
                [lines[0], "{not-json}", .. lines.Skip(1)],
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                TestContext.CancellationToken);
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "repair", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--yes", "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.GeneralError, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("blocked", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbRepair_BackupFailure_DoesNotStartRepair()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await AppendDamagedTailAsync(fixture.MessagePath);
            await File.WriteAllTextAsync(
                Path.Combine(fixture.DataDirectory, "backups"),
                "blocks backup directory creation",
                TestContext.CancellationToken);
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "repair", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--yes", "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.GeneralError, exitCode, output.ToString());
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbRepair_RepairWriteFailure_RetainsRecoveryPoint()
    {
        var fixture = await CreateFixtureAsync();
        var originalAttributes = File.GetAttributes(fixture.MessagePath);
        try
        {
            await AppendDamagedTailAsync(fixture.MessagePath);
            var damagedBytes = await File.ReadAllBytesAsync(
                fixture.MessagePath,
                TestContext.CancellationToken);
            File.SetAttributes(fixture.MessagePath, originalAttributes | FileAttributes.ReadOnly);

            var exception = await Assert.ThrowsExactlyAsync<DatabaseRepairException>(
                () => DatabaseMaintenanceService.RepairAsync(
                    fixture.Workspace,
                    fixture.DataDirectory,
                    TestContext.CancellationToken));

            Assert.IsNotNull(exception.BackupPath);
            Assert.Contains("Recovery point retained", exception.Message, StringComparison.Ordinal);
            Assert.IsTrue(File.Exists(Path.Combine(exception.BackupPath, "state.db")));
            var backupMessagePath = Path.Combine(
                exception.BackupPath,
                "messages",
                fixture.SessionId + ".jsonl");
            Assert.IsTrue(File.Exists(backupMessagePath));
            CollectionAssert.AreEqual(
                damagedBytes,
                await File.ReadAllBytesAsync(backupMessagePath, TestContext.CancellationToken));
        }
        finally
        {
            if (File.Exists(fixture.MessagePath))
            {
                File.SetAttributes(fixture.MessagePath, originalAttributes);
            }

            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbRebuild_DryRun_IsReadOnlyAndDoesNotAcquireWorkspaceLock()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var workspaceLock = await WorkspaceWriteLock.AcquireAsync(
                fixture.Workspace,
                "rebuild-dry-run-lock-holder",
                ct: TestContext.CancellationToken);
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "rebuild", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--dry-run", "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("dry-run", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            Assert.AreEqual(1, document.RootElement.GetProperty("data").GetProperty("sessionsScanned").GetInt32());
            Assert.AreEqual(1, document.RootElement.GetProperty("data").GetProperty("sessionsRecoverable").GetInt32());
            Assert.AreEqual(1, document.RootElement.GetProperty("data").GetProperty("messagesRecoverable").GetInt32());
            Assert.AreEqual(JsonValueKind.Null, document.RootElement.GetProperty("data").GetProperty("backupPath").ValueKind);
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbRebuild_JsonWithoutConfirm_ReturnsInvalidArgumentsWithoutWriting()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "rebuild", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.InvalidArguments, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("error", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            var message = document.RootElement.GetProperty("data").GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains(
                "--confirm is required",
                message,
                StringComparison.Ordinal);
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbRebuild_DeclinedConfirmation_PreservesOriginalData()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "rebuild", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory],
                output,
                new StringReader("n" + Environment.NewLine),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.UserInterrupted, exitCode, output.ToString());
            Assert.Contains("No changes were made", output.ToString(), StringComparison.Ordinal);
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbRebuild_WorkspaceLockConflict_PreservesOriginalData()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var workspaceLock = await WorkspaceWriteLock.AcquireAsync(
                fixture.Workspace,
                "rebuild-lock-holder",
                ct: TestContext.CancellationToken);
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "rebuild", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--confirm", "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.WorkspaceError, exitCode, output.ToString());
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbRebuild_Confirmed_ReplacesDatabaseAndRestoresRecoveredSession()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await ExecuteAsync(
                fixture.DataDirectory,
                """
                INSERT INTO sessions(session_id, mode, status, created_at, updated_at)
                VALUES($sessionId, 'Expert', 'Active', '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
                """,
                "orphan-session");
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "rebuild", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--confirm", "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("ok", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            Assert.AreEqual(1, document.RootElement.GetProperty("data").GetProperty("sessionsScanned").GetInt32());
            Assert.AreEqual(1, document.RootElement.GetProperty("data").GetProperty("sessionsRebuilt").GetInt32());
            Assert.AreEqual(1, document.RootElement.GetProperty("data").GetProperty("messagesUpserted").GetInt32());
            var backupPath = document.RootElement.GetProperty("data").GetProperty("backupPath").GetString();
            Assert.IsNotNull(backupPath);
            Assert.IsTrue(File.Exists(Path.Combine(backupPath, "state.db")));
            Assert.AreEqual(
                1L,
                await ExecuteScalarAsync(
                    fixture.DataDirectory,
                    "SELECT COUNT(*) FROM sessions WHERE session_id = $sessionId;",
                    fixture.SessionId));
            Assert.AreEqual(
                0L,
                await ExecuteScalarAsync(
                    fixture.DataDirectory,
                    "SELECT COUNT(*) FROM sessions WHERE session_id = $sessionId;",
                    "orphan-session"));
            Assert.AreEqual(
                "Recovered",
                await ExecuteTextScalarFromDatabaseAsync(
                    Path.Combine(fixture.DataDirectory, "state.db"),
                    $"SELECT status FROM sessions WHERE session_id = '{fixture.SessionId}';"));
            Assert.AreEqual(
                1L,
                await ExecuteScalarAsync(
                    fixture.DataDirectory,
                    "SELECT COUNT(*) FROM message_index WHERE session_id = $sessionId;",
                    fixture.SessionId));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbRebuild_MiddleDamage_ReportsPartialAndPreservesCanonicalFiles()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            const string damagedSessionId = "rebuild-middle-damage";
            using (var content = JsonDocument.Parse("[{\"type\":\"text\",\"text\":\"damaged\"}]"))
            {
                var store = new ConversationStore(fixture.DataDirectory);
                await store.AppendMessageAsync(
                    damagedSessionId,
                    "Expert",
                    new ConversationMessageDraft(
                        "invocation-rebuild-damaged",
                        "agent-rebuild-damaged",
                        "user",
                        content.RootElement.Clone(),
                        DateTimeOffset.UtcNow),
                    TestContext.CancellationToken);
            }

            var damagedPath = Path.Combine(
                fixture.DataDirectory,
                "messages",
                damagedSessionId + ".jsonl");
            var lines = await File.ReadAllLinesAsync(damagedPath, TestContext.CancellationToken);
            await File.WriteAllLinesAsync(
                damagedPath,
                [lines[0], "{not-json}", .. lines.Skip(1)],
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                TestContext.CancellationToken);
            var damagedBytes = await File.ReadAllBytesAsync(
                damagedPath,
                TestContext.CancellationToken);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "rebuild", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--confirm", "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.GeneralError, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("partial", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            Assert.AreEqual(2, document.RootElement.GetProperty("data").GetProperty("sessionsScanned").GetInt32());
            Assert.AreEqual(1, document.RootElement.GetProperty("data").GetProperty("sessionsRebuilt").GetInt32());
            Assert.IsTrue(document.RootElement.GetProperty("data").GetProperty("unrecoverableDiagnostics")
                .EnumerateArray()
                .Any(item => item.GetString()?.Contains(damagedSessionId, StringComparison.Ordinal) == true));
            Assert.IsTrue(Directory.Exists(document.RootElement.GetProperty("data").GetProperty("backupPath").GetString()));
            CollectionAssert.AreEqual(
                damagedBytes,
                await File.ReadAllBytesAsync(damagedPath, TestContext.CancellationToken));
            Assert.AreEqual(
                0L,
                await ExecuteScalarAsync(
                    fixture.DataDirectory,
                    "SELECT COUNT(*) FROM sessions WHERE session_id = $sessionId;",
                    damagedSessionId));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbRebuild_BackupFailure_DoesNotModifyDatabase()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(fixture.DataDirectory, "backups"),
                "blocks rebuild recovery point",
                TestContext.CancellationToken);
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "rebuild", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--confirm", "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.GeneralError, exitCode, output.ToString());
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbRebuild_ExecutionFailure_RetainsRecoveryPoint()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var recoveryReportPath = Path.Combine(
                fixture.DataDirectory,
                "logs",
                "startup-recovery.json");
            var output = new StringWriter();
            using (var lockedReport = new FileStream(
                       recoveryReportPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.None))
            {
                var exitCode = Run(
                    ["db", "rebuild", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--confirm", "--json"],
                    output,
                    new StringReader(string.Empty),
                    fixture.ConfigDirectory);

                Assert.AreEqual(ExitCodes.GeneralError, exitCode, output.ToString());
                Assert.IsGreaterThan(0, lockedReport.Length);
            }

            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("error", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            var backupPath = document.RootElement.GetProperty("data").GetProperty("backupPath").GetString();
            Assert.IsNotNull(backupPath);
            Assert.IsTrue(File.Exists(Path.Combine(backupPath, "state.db")));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbVacuum_WithFreePages_CompactsDatabaseAndReportsBytes()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await ExecuteAsync(
                fixture.DataDirectory,
                """
                PRAGMA wal_checkpoint(TRUNCATE);
                CREATE TABLE vacuum_payload(payload BLOB NOT NULL);
                INSERT INTO vacuum_payload(payload) VALUES(zeroblob(8388608));
                DELETE FROM vacuum_payload;
                PRAGMA wal_checkpoint(TRUNCATE);
                """,
                fixture.SessionId);
            var databasePath = Path.Combine(fixture.DataDirectory, "state.db");
            var bytesBefore = new FileInfo(databasePath).Length;
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "vacuum", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("db vacuum", document.RootElement.GetProperty("data").GetProperty("command").GetString());
            Assert.AreEqual("ok", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            Assert.AreEqual(databasePath, document.RootElement.GetProperty("data").GetProperty("databasePath").GetString());
            Assert.AreEqual(bytesBefore, document.RootElement.GetProperty("data").GetProperty("bytesBefore").GetInt64());
            var bytesAfter = document.RootElement.GetProperty("data").GetProperty("bytesAfter").GetInt64();
            Assert.IsLessThan(bytesBefore, bytesAfter);
            Assert.AreEqual(
                bytesBefore - bytesAfter,
                document.RootElement.GetProperty("data").GetProperty("bytesReclaimed").GetInt64());
            Assert.AreEqual(bytesAfter, new FileInfo(databasePath).Length);
            Assert.AreEqual(
                0L,
                await ExecuteScalarFromDatabaseAsync(databasePath, "PRAGMA freelist_count;"));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbVacuum_WorkspaceLockConflict_PreservesDatabase()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var workspaceLock = await WorkspaceWriteLock.AcquireAsync(
                fixture.Workspace,
                "vacuum-lock-holder",
                ct: TestContext.CancellationToken);
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "vacuum", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.WorkspaceError, exitCode, output.ToString());
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbVacuum_MissingDatabase_ReturnsErrorWithoutCreatingFiles()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            File.Delete(Path.Combine(fixture.DataDirectory, "state.db"));
            File.Delete(Path.Combine(fixture.DataDirectory, "state.db-wal"));
            File.Delete(Path.Combine(fixture.DataDirectory, "state.db-shm"));
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "vacuum", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.GeneralError, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("error", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbMigrate_DryRun_FromPreviousVersion_IsReadOnlyWithoutLock()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await DowngradeToVersionThirteenAsync(fixture);
            await using var workspaceLock = await WorkspaceWriteLock.AcquireAsync(
                fixture.Workspace,
                "migration-dry-run-lock-holder",
                ct: TestContext.CancellationToken);
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "migrate", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--dry-run", "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("db migrate", document.RootElement.GetProperty("data").GetProperty("command").GetString());
            Assert.AreEqual("dry-run", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            Assert.AreEqual(13, document.RootElement.GetProperty("data").GetProperty("fromVersion").GetInt32());
            Assert.AreEqual(14, document.RootElement.GetProperty("data").GetProperty("targetVersion").GetInt32());
            var pendingVersions = document.RootElement.GetProperty("data").GetProperty("pendingVersions")
                .EnumerateArray()
                .Select(static value => value.GetInt32())
                .ToArray();
            Assert.HasCount(1, pendingVersions);
            Assert.AreEqual(14, pendingVersions[0]);
            Assert.AreEqual(JsonValueKind.Null, document.RootElement.GetProperty("data").GetProperty("backupPath").ValueKind);
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbMigrate_FromPreviousVersion_CreatesRecoveryPointAndPreservesSession()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await DowngradeToVersionThirteenAsync(fixture);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "migrate", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--to", "14", "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("migrated", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            Assert.AreEqual(13, document.RootElement.GetProperty("data").GetProperty("fromVersion").GetInt32());
            Assert.AreEqual(14, document.RootElement.GetProperty("data").GetProperty("targetVersion").GetInt32());
            var appliedVersions = document.RootElement.GetProperty("data").GetProperty("appliedVersions")
                .EnumerateArray()
                .Select(static value => value.GetInt32())
                .ToArray();
            Assert.HasCount(1, appliedVersions);
            Assert.AreEqual(14, appliedVersions[0]);
            var backupPath = document.RootElement.GetProperty("data").GetProperty("backupPath").GetString();
            Assert.IsNotNull(backupPath);
            Assert.StartsWith(
                Path.Combine(fixture.DataDirectory, "backups", "migrate-"),
                backupPath,
                StringComparison.Ordinal);
            Assert.IsTrue(File.Exists(Path.Combine(backupPath, "state.db")));
            Assert.AreEqual(
                13L,
                await ExecuteScalarFromDatabaseAsync(
                    Path.Combine(backupPath, "state.db"),
                    "SELECT MAX(version) FROM schema_versions;"));
            Assert.AreEqual(
                14L,
                await ExecuteScalarAsync(
                    fixture.DataDirectory,
                    "SELECT MAX(version) FROM schema_versions;",
                    fixture.SessionId));
            Assert.AreEqual(
                1L,
                await ExecuteScalarAsync(
                    fixture.DataDirectory,
                    "SELECT COUNT(*) FROM sessions WHERE session_id = $sessionId;",
                    fixture.SessionId));
            Assert.AreEqual(
                1L,
                await ExecuteScalarAsync(
                    fixture.DataDirectory,
                    "SELECT COUNT(*) FROM pragma_table_info('sessions') WHERE name = 'title';",
                    fixture.SessionId));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbMigrate_WorkspaceLockConflict_PreservesDatabase()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await DowngradeToVersionThirteenAsync(fixture);
            await using var workspaceLock = await WorkspaceWriteLock.AcquireAsync(
                fixture.Workspace,
                "migration-lock-holder",
                ct: TestContext.CancellationToken);
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "migrate", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.WorkspaceError, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("error", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbMigrate_BackupFailure_DoesNotChangeSchema()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await DowngradeToVersionThirteenAsync(fixture);
            await File.WriteAllTextAsync(
                Path.Combine(fixture.DataDirectory, "backups"),
                "blocks migration recovery point",
                TestContext.CancellationToken);
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "migrate", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.GeneralError, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("error", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            Assert.AreEqual(JsonValueKind.Null, document.RootElement.GetProperty("data").GetProperty("backupPath").ValueKind);
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
            Assert.AreEqual(
                13L,
                await ExecuteScalarAsync(
                    fixture.DataDirectory,
                    "SELECT MAX(version) FROM schema_versions;",
                    fixture.SessionId));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbMigrate_MigrationFailure_RetainsRecoveryPoint()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await DowngradeToVersionThirteenAsync(fixture);
            await ExecuteAsync(
                fixture.DataDirectory,
                "PRAGMA foreign_keys = OFF; DROP TABLE sessions; PRAGMA wal_checkpoint(TRUNCATE);",
                fixture.SessionId);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "migrate", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.GeneralError, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("error", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            var backupPath = document.RootElement.GetProperty("data").GetProperty("backupPath").GetString();
            Assert.IsNotNull(backupPath);
            var message = document.RootElement.GetProperty("data").GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("Recovery point retained", message);
            Assert.IsTrue(File.Exists(Path.Combine(backupPath, "state.db")));
            Assert.AreEqual(
                13L,
                await ExecuteScalarFromDatabaseAsync(
                    Path.Combine(backupPath, "state.db"),
                    "SELECT MAX(version) FROM schema_versions;"));
            Assert.AreEqual(
                13L,
                await ExecuteScalarAsync(
                    fixture.DataDirectory,
                    "SELECT MAX(version) FROM schema_versions;",
                    fixture.SessionId));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbMigrate_ExplicitIntermediateTarget_StopsAtRequestedVersion()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var dataDirectory = Path.Combine(root, "data");
            var configDirectory = Path.Combine(root, "config");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(dataDirectory);
            var databasePath = Path.Combine(dataDirectory, "state.db");
            await using (var connection = new SqliteConnection(
                             $"Data Source={databasePath};Mode=ReadWriteCreate;Pooling=False"))
            {
                await connection.OpenAsync(TestContext.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    "CREATE TABLE migration_bootstrap(value INTEGER); DROP TABLE migration_bootstrap;";
                await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
            }

            var output = new StringWriter();

            var exitCode = Run(
                ["db", "migrate", "--workspace", workspace, "--data-dir", dataDirectory, "--to", "1", "--json"],
                output,
                new StringReader(string.Empty),
                configDirectory);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("migrated", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            Assert.AreEqual(0, document.RootElement.GetProperty("data").GetProperty("fromVersion").GetInt32());
            Assert.AreEqual(1, document.RootElement.GetProperty("data").GetProperty("targetVersion").GetInt32());
            Assert.AreEqual(
                1L,
                await ExecuteScalarFromDatabaseAsync(
                    databasePath,
                    "SELECT MAX(version) FROM schema_versions;"));
            Assert.AreEqual(
                1L,
                await ExecuteScalarFromDatabaseAsync(
                    databasePath,
                    "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'sessions';"));
            Assert.AreEqual(
                0L,
                await ExecuteScalarFromDatabaseAsync(
                    databasePath,
                    "SELECT COUNT(*) FROM schema_versions WHERE version > 1;"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task DbMigrate_CurrentVersion_ReturnsUpToDateWithoutBackup()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "migrate", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("up-to-date", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            Assert.AreEqual(14, document.RootElement.GetProperty("data").GetProperty("fromVersion").GetInt32());
            Assert.AreEqual(14, document.RootElement.GetProperty("data").GetProperty("targetVersion").GetInt32());
            Assert.IsEmpty(document.RootElement.GetProperty("data").GetProperty("appliedVersions").EnumerateArray());
            Assert.AreEqual(JsonValueKind.Null, document.RootElement.GetProperty("data").GetProperty("backupPath").ValueKind);
            Assert.IsFalse(Directory.Exists(Path.Combine(fixture.DataDirectory, "backups")));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(13)]
    [DataRow(15)]
    public async Task DbMigrate_InvalidTarget_ReturnsInvalidArgumentsWithoutWriting(int targetVersion)
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "migrate", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--to", targetVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.InvalidArguments, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("error", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbMigrate_MissingDatabase_ReturnsErrorWithoutCreatingFiles()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            File.Delete(Path.Combine(fixture.DataDirectory, "state.db"));
            File.Delete(Path.Combine(fixture.DataDirectory, "state.db-wal"));
            File.Delete(Path.Combine(fixture.DataDirectory, "state.db-shm"));
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "migrate", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.GeneralError, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("error", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbBackup_ActiveWal_CreatesConsistentOnlineBackup()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var writerConnection = new SqliteConnection(
                $"Data Source={Path.Combine(fixture.DataDirectory, "state.db")};Pooling=False");
            await writerConnection.OpenAsync(TestContext.CancellationToken);
            await using (var command = writerConnection.CreateCommand())
            {
                command.CommandText = """
                    PRAGMA wal_autocheckpoint = 0;
                    INSERT INTO sessions(session_id, mode, status, created_at, updated_at)
                    VALUES('backup-wal-session', 'Expert', 'Active', $now, $now);
                    """;
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
            }

            var backupPath = Path.Combine(fixture.Root, "备份 输出", "runtime state.db");
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "backup", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--output", backupPath, "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            Assert.IsTrue(File.Exists(backupPath));
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual("ok", document.RootElement.GetProperty("data").GetProperty("status").GetString());
            Assert.AreEqual(Path.GetFullPath(backupPath), document.RootElement.GetProperty("data").GetProperty("databasePath").GetString());
            Assert.AreEqual(
                1L,
                await ExecuteScalarFromDatabaseAsync(
                    backupPath,
                    "SELECT COUNT(*) FROM sessions WHERE session_id = 'backup-wal-session';"));
            Assert.AreEqual(
                "ok",
                await ExecuteTextScalarFromDatabaseAsync(backupPath, "PRAGMA quick_check;"));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbBackup_IncludeMessages_ArchivesCanonicalBytes()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var nestedDirectory = Path.Combine(fixture.DataDirectory, "messages", "投影 缓存");
            Directory.CreateDirectory(nestedDirectory);
            var nestedPath = Path.Combine(nestedDirectory, "compact 01.json");
            var nestedBytes = Encoding.UTF8.GetBytes("{\"摘要\":\"保持 原始字节\"}");
            await File.WriteAllBytesAsync(
                nestedPath,
                nestedBytes,
                TestContext.CancellationToken);
            var backupPath = Path.Combine(fixture.Root, "backup", "runtime.db");
            var archivePath = Path.ChangeExtension(backupPath, ".messages.zip");
            var expectedBytes = await File.ReadAllBytesAsync(
                fixture.MessagePath,
                TestContext.CancellationToken);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "backup", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--output", backupPath, "--include-messages", "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            Assert.IsTrue(File.Exists(backupPath));
            Assert.IsTrue(File.Exists(archivePath));
            using var document = JsonDocument.Parse(output.ToString());
            Assert.AreEqual(archivePath, document.RootElement.GetProperty("data").GetProperty("messagesArchivePath").GetString());
            Assert.AreEqual(2, document.RootElement.GetProperty("data").GetProperty("messageFileCount").GetInt32());
            using var archive = ZipFile.OpenRead(archivePath);
            Assert.HasCount(2, archive.Entries);
            var canonicalEntry = archive.GetEntry(fixture.SessionId + ".jsonl");
            Assert.IsNotNull(canonicalEntry);
            await using var entryStream = canonicalEntry.Open();
            using var buffer = new MemoryStream();
            await entryStream.CopyToAsync(buffer, TestContext.CancellationToken);
            CollectionAssert.AreEqual(expectedBytes, buffer.ToArray());

            var nestedEntry = archive.GetEntry("投影 缓存/compact 01.json");
            Assert.IsNotNull(nestedEntry);
            await using var nestedStream = nestedEntry.Open();
            using var nestedBuffer = new MemoryStream();
            await nestedStream.CopyToAsync(nestedBuffer, TestContext.CancellationToken);
            CollectionAssert.AreEqual(nestedBytes, nestedBuffer.ToArray());
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbBackup_ExistingOutput_ReturnsErrorWithoutOverwriting()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var backupPath = Path.Combine(fixture.Root, "existing.db");
            var originalBytes = Encoding.UTF8.GetBytes("existing backup must remain");
            await File.WriteAllBytesAsync(
                backupPath,
                originalBytes,
                TestContext.CancellationToken);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "backup", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--output", backupPath, "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.GeneralError, exitCode, output.ToString());
            CollectionAssert.AreEqual(
                originalBytes,
                await File.ReadAllBytesAsync(backupPath, TestContext.CancellationToken));
            Assert.IsEmpty(Directory.EnumerateFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbBackup_DefaultOutput_CreatesUniqueDatabaseUnderBackups()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "backup", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            var databasePath = document.RootElement.GetProperty("data").GetProperty("databasePath").GetString();
            Assert.IsNotNull(databasePath);
            Assert.AreEqual(
                Path.Combine(fixture.DataDirectory, "backups"),
                Path.GetDirectoryName(databasePath));
            Assert.AreEqual(".db", Path.GetExtension(databasePath));
            Assert.IsTrue(File.Exists(databasePath));
            Assert.AreEqual(JsonValueKind.Null, document.RootElement.GetProperty("data").GetProperty("messagesArchivePath").ValueKind);
            Assert.AreEqual(0, document.RootElement.GetProperty("data").GetProperty("messageFileCount").GetInt32());
            Assert.AreEqual(
                "ok",
                await ExecuteTextScalarFromDatabaseAsync(databasePath, "PRAGMA quick_check;"));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbBackup_DatabaseOnly_DoesNotAcquireWorkspaceLock()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var backupPath = Path.Combine(fixture.Root, "database-only.db");
            await using var workspaceLock = await WorkspaceWriteLock.AcquireAsync(
                fixture.Workspace,
                "database-only-lock-holder",
                ct: TestContext.CancellationToken);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "backup", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--output", backupPath, "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            Assert.IsTrue(File.Exists(backupPath));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbBackup_MessageArchiveFailure_CleansIncompleteBackupSet()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var backupPath = Path.Combine(fixture.Root, "archive-failure.db");
            var archivePath = Path.ChangeExtension(backupPath, ".messages.zip");
            var output = new StringWriter();
            using (var lockedMessage = new FileStream(
                       fixture.MessagePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.None))
            {
                var exitCode = Run(
                    ["db", "backup", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--output", backupPath, "--include-messages", "--json"],
                    output,
                    new StringReader(string.Empty),
                    fixture.ConfigDirectory);

                Assert.AreEqual(ExitCodes.GeneralError, exitCode, output.ToString());
                Assert.IsGreaterThan(0, lockedMessage.Length);
            }

            Assert.IsFalse(File.Exists(backupPath));
            Assert.IsFalse(File.Exists(archivePath));
            Assert.IsEmpty(Directory.EnumerateFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task DbBackup_IncludeMessagesWithWorkspaceLockConflict_LeavesNoOutput()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var backupPath = Path.Combine(fixture.Root, "locked.db");
            await using var workspaceLock = await WorkspaceWriteLock.AcquireAsync(
                fixture.Workspace,
                "backup-lock-holder",
                ct: TestContext.CancellationToken);
            var output = new StringWriter();

            var exitCode = Run(
                ["db", "backup", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--output", backupPath, "--include-messages", "--json"],
                output,
                new StringReader(string.Empty),
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.WorkspaceError, exitCode, output.ToString());
            Assert.IsFalse(File.Exists(backupPath));
            Assert.IsFalse(File.Exists(Path.ChangeExtension(backupPath, ".messages.zip")));
            Assert.IsEmpty(Directory.EnumerateFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    private async Task<DatabaseFixture> CreateFixtureAsync()
    {
        var root = CreateTemporaryDirectory();
        var workspace = Path.Combine(root, "workspace");
        var dataDirectory = Path.Combine(root, "data");
        var configDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(workspace);

        string sessionId;
        await using (var connection = await DataDirectoryInitializer.InitializeAsync(
            dataDirectory,
            TestContext.CancellationToken))
        {
            var repository = new SqliteSessionRepository(connection);
            sessionId = await repository.CreateSessionAsync(
                RuntimeMode.Expert,
                "database-check-session",
                TimeSpan.FromHours(1),
                TestContext.CancellationToken);
        }

        using var content = JsonDocument.Parse(
            $"[{{\"type\":\"text\",\"text\":\"{JsonEncodedText.Encode("database check")}\"}}]");
        var store = new ConversationStore(dataDirectory);
        await store.AppendMessageAsync(
            sessionId,
            "Expert",
            new ConversationMessageDraft(
                "invocation-database-check",
                "agent-database-check",
                "user",
                content.RootElement.Clone(),
                DateTimeOffset.UtcNow),
            TestContext.CancellationToken);

        return new DatabaseFixture(
            root,
            workspace,
            dataDirectory,
            configDirectory,
            sessionId,
            Path.Combine(dataDirectory, "messages", sessionId + ".jsonl"));
    }

    private static int Run(
        string[] args,
        TextWriter output,
        TextReader input,
        string configDirectory) =>
        CliApplication.RunForTests(
            args,
            output,
            input,
            configDirectory,
            static _ => static _ => throw new InvalidOperationException(
                "Provider resolution was not expected."));

    private async Task AppendDamagedTailAsync(string messagePath) =>
        await File.AppendAllTextAsync(
            messagePath,
            "{\"incomplete\":",
            Encoding.UTF8,
            TestContext.CancellationToken);

    private async Task DowngradeToVersionThirteenAsync(DatabaseFixture fixture) =>
        await ExecuteAsync(
            fixture.DataDirectory,
            """
            PRAGMA foreign_keys = OFF;
            ALTER TABLE sessions DROP COLUMN title;
            DELETE FROM schema_versions WHERE version = 14;
            PRAGMA wal_checkpoint(TRUNCATE);
            """,
            fixture.SessionId);

    private async Task ExecuteAsync(string dataDirectory, string sql, string sessionId)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(dataDirectory, "state.db")};Pooling=False");
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
    }

    private async Task<long> ExecuteScalarAsync(
        string dataDirectory,
        string sql,
        string sessionId)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(dataDirectory, "state.db")};Pooling=False");
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(TestContext.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<long> ExecuteScalarFromDatabaseAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(TestContext.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<string?> ExecuteTextScalarFromDatabaseAsync(
        string databasePath,
        string sql)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(
            await command.ExecuteScalarAsync(TestContext.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<TreeSnapshot> CaptureTreeAsync(string root)
    {
        var directories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var files = new List<FileSnapshot>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            var bytes = await ReadAllBytesSharedAsync(path);
            files.Add(new FileSnapshot(
                Path.GetRelativePath(root, path),
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)),
                File.GetLastWriteTimeUtc(path)));
        }

        return new TreeSnapshot(directories, [.. files]);
    }

    private static async Task<byte[]> ReadAllBytesSharedAsync(string path)
    {
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private async Task CopySharedFileAsync(string sourcePath, string destinationPath)
    {
        var bytes = await ReadAllBytesSharedAsync(sourcePath);
        await File.WriteAllBytesAsync(destinationPath, bytes, TestContext.CancellationToken);
    }

    private static void AssertTreeEqual(TreeSnapshot expected, TreeSnapshot actual)
    {
        CollectionAssert.AreEqual(
            expected.Directories,
            actual.Directories,
            $"Expected directories: {string.Join(", ", expected.Directories)}{Environment.NewLine}Actual directories: {string.Join(", ", actual.Directories)}");
        CollectionAssert.AreEqual(
            expected.Files,
            actual.Files,
            $"Expected files: {string.Join(", ", expected.Files.Select(static file => file.RelativePath))}{Environment.NewLine}Actual files: {string.Join(", ", actual.Files.Select(static file => file.RelativePath))}");
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-stage7-database-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record DatabaseFixture(
        string Root,
        string Workspace,
        string DataDirectory,
        string ConfigDirectory,
        string SessionId,
        string MessagePath);

    private sealed record TreeSnapshot(string[] Directories, FileSnapshot[] Files);

    private sealed record FileSnapshot(
        string RelativePath,
        long Length,
        string Sha256,
        DateTime LastWriteTimeUtc);
}
