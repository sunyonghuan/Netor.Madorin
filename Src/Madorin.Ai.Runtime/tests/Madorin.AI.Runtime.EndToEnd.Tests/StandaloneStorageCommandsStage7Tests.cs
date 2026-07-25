using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Cli;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Server;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
[DoNotParallelize]
public sealed class StandaloneStorageCommandsStage7Tests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public void StorageCommand_FreezesCheckAndGcSurface()
    {
        var root = CliApplication.CreateRootCommand(
            new StringWriter(),
            new StringReader(string.Empty));
        var storage = root.Subcommands.Single(static command => command.Name == "storage");
        var check = storage.Subcommands.Single(static command => command.Name == "check");
        var gc = storage.Subcommands.Single(static command => command.Name == "gc");

        Assert.Contains("--verify-hashes", check.Options.Select(static option => option.Name));
        Assert.DoesNotContain("--repair", check.Options.Select(static option => option.Name));
        Assert.DoesNotContain("--yes", check.Options.Select(static option => option.Name));
        Assert.Contains("--dry-run", gc.Options.Select(static option => option.Name));
        Assert.Contains("--older-than", gc.Options.Select(static option => option.Name));
        Assert.Contains("--confirm", gc.Options.Select(static option => option.Name));
        Assert.DoesNotContain("--older-than-days", gc.Options.Select(static option => option.Name));
    }

    [TestMethod]
    public async Task StorageCheck_HealthyReferencedBlobs_DoesNotWriteAnything()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["storage", "check", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--json"],
                output,
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            var data = document.RootElement.GetProperty("data");
            Assert.AreEqual("ok", data.GetProperty("status").GetString());
            Assert.IsTrue(data.GetProperty("healthy").GetBoolean());
            Assert.AreEqual(2, data.GetProperty("referenceCount").GetInt32());
            Assert.AreEqual(2, data.GetProperty("blobFileCount").GetInt32());
            Assert.AreEqual(0, data.GetProperty("issueCount").GetInt32());
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task StorageCheck_MissingReferenceAndOrphan_ReportStableIssuesWithoutWriting()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            File.Delete(fixture.MessageBlobPath);
            var orphan = await CreateBlobFileAsync(fixture, "orphan", ageDays: 10);
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["storage", "check", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--json"],
                output,
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.GeneralError, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            var data = document.RootElement.GetProperty("data");
            Assert.AreEqual("issues", data.GetProperty("status").GetString());
            var issues = data.GetProperty("issues").EnumerateArray().ToArray();
            Assert.IsTrue(issues.Any(issue =>
                issue.GetProperty("code").GetString() == "referenced_blob_missing"
                && issue.GetProperty("blobId").GetString() == fixture.MessageBlobId));
            Assert.IsTrue(issues.Any(issue =>
                issue.GetProperty("code").GetString() == "orphan_blob"
                && issue.GetProperty("blobId").GetString() == orphan.BlobId));
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task StorageCheck_VerifyHashes_DetectsSameLengthCorruptionOnlyWhenRequested()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var original = await File.ReadAllBytesAsync(
                fixture.MessageBlobPath,
                TestContext.CancellationToken);
            var corrupted = original.Select(static value => (byte)(value ^ 0x5A)).ToArray();
            await File.WriteAllBytesAsync(
                fixture.MessageBlobPath,
                corrupted,
                TestContext.CancellationToken);

            var fastOutput = new StringWriter();
            var fastExitCode = Run(
                ["storage", "check", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--json"],
                fastOutput,
                fixture.ConfigDirectory);
            Assert.AreEqual(ExitCodes.Success, fastExitCode, fastOutput.ToString());

            var verifiedOutput = new StringWriter();
            var verifiedExitCode = Run(
                ["storage", "check", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--verify-hashes", "--json"],
                verifiedOutput,
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.GeneralError, verifiedExitCode, verifiedOutput.ToString());
            using var document = JsonDocument.Parse(verifiedOutput.ToString());
            Assert.IsTrue(document.RootElement.GetProperty("data").GetProperty("issues")
                .EnumerateArray()
                .Any(issue => issue.GetProperty("code").GetString() == "blob_hash_mismatch"));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task StorageGc_DefaultDryRun_ReportsOldOrphanWithoutLockingOrWriting()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var orphan = await CreateBlobFileAsync(fixture, "old orphan", ageDays: 10);
            await using var workspaceLock = await WorkspaceWriteLock.AcquireAsync(
                fixture.Workspace,
                "storage-dry-run-holder",
                ct: TestContext.CancellationToken);
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["storage", "gc", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--json"],
                output,
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            var data = document.RootElement.GetProperty("data");
            Assert.AreEqual("dry-run", data.GetProperty("status").GetString());
            Assert.AreEqual(7, data.GetProperty("olderThanDays").GetInt32());
            Assert.AreEqual(1, data.GetProperty("candidateCount").GetInt32());
            Assert.AreEqual(orphan.BlobId, data.GetProperty("candidateBlobIds")[0].GetString());
            Assert.IsTrue(File.Exists(orphan.Path));
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task StorageGc_Confirm_MovesOnlyOldOrphansIntoRecoveryPoint()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var oldOrphan = await CreateBlobFileAsync(fixture, "old orphan", ageDays: 10);
            var youngOrphan = await CreateBlobFileAsync(fixture, "young orphan", ageDays: 1);
            var output = new StringWriter();

            var exitCode = Run(
                ["storage", "gc", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--confirm", "--json"],
                output,
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            var data = document.RootElement.GetProperty("data");
            Assert.AreEqual("collected", data.GetProperty("status").GetString());
            Assert.AreEqual(1, data.GetProperty("collectedCount").GetInt32());
            Assert.AreEqual(1, data.GetProperty("protectedByAgeCount").GetInt32());
            var recoveryPoint = data.GetProperty("recoveryPointPath").GetString();
            Assert.IsNotNull(recoveryPoint);
            Assert.IsFalse(File.Exists(oldOrphan.Path));
            Assert.IsTrue(File.Exists(youngOrphan.Path));
            Assert.IsTrue(File.Exists(fixture.MessageBlobPath));
            Assert.IsTrue(File.Exists(fixture.ToolBlobPath));
            Assert.IsTrue(File.Exists(Path.Combine(recoveryPoint, "blobs", oldOrphan.BlobId + ".blob")));
            Assert.IsTrue(File.Exists(Path.Combine(recoveryPoint, "manifest.txt")));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task StorageGc_InvalidArguments_DoNotWrite()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var orphan = await CreateBlobFileAsync(fixture, "invalid argument orphan", ageDays: 10);
            var before = await CaptureTreeAsync(fixture.Root);

            foreach (var args in new[]
                     {
                         new[] { "storage", "gc", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--older-than", "-1", "--json" },
                         new[] { "storage", "gc", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--dry-run", "--confirm", "--json" }
                     })
            {
                var output = new StringWriter();
                var exitCode = Run(args, output, fixture.ConfigDirectory);
                Assert.AreEqual(ExitCodes.InvalidArguments, exitCode, output.ToString());
                using var document = JsonDocument.Parse(output.ToString());
                Assert.AreEqual(
                    "error",
                    document.RootElement.GetProperty("data").GetProperty("status").GetString());
            }

            Assert.IsTrue(File.Exists(orphan.Path));
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task StorageGc_LockConflict_DoesNotCreateRecoveryPointOrMoveBlob()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var orphan = await CreateBlobFileAsync(fixture, "locked orphan", ageDays: 10);
            await using var workspaceLock = await WorkspaceWriteLock.AcquireAsync(
                fixture.Workspace,
                "storage-gc-holder",
                ct: TestContext.CancellationToken);
            var output = new StringWriter();

            var exitCode = Run(
                ["storage", "gc", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--confirm", "--json"],
                output,
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.WorkspaceError, exitCode, output.ToString());
            Assert.IsTrue(File.Exists(orphan.Path));
            Assert.IsFalse(Directory.Exists(Path.Combine(fixture.DataDirectory, "backups")));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task StorageGc_RecoveryPointFailure_DoesNotMoveBlob()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var orphan = await CreateBlobFileAsync(fixture, "backup failure orphan", ageDays: 10);
            await File.WriteAllTextAsync(
                Path.Combine(fixture.DataDirectory, "backups"),
                "blocks recovery directory creation",
                TestContext.CancellationToken);
            var output = new StringWriter();

            var exitCode = Run(
                ["storage", "gc", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--confirm", "--json"],
                output,
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.GeneralError, exitCode, output.ToString());
            Assert.IsTrue(File.Exists(orphan.Path));
            using var document = JsonDocument.Parse(output.ToString());
            var data = document.RootElement.GetProperty("data");
            Assert.AreEqual("error", data.GetProperty("status").GetString());
            Assert.AreEqual(JsonValueKind.Null, data.GetProperty("recoveryPointPath").ValueKind);
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task StorageGc_MidMoveFailure_RetainsRecoveryPointAndRemainingSource()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var first = await CreateBlobFileAsync(fixture, "a-first", ageDays: 10);
            var second = await CreateBlobFileAsync(fixture, "z-second", ageDays: 10);
            var ordered = new[] { first, second }.OrderBy(static item => item.BlobId, StringComparer.Ordinal).ToArray();
            var output = new StringWriter();

            using (var locked = new FileStream(
                       ordered[1].Path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            {
                var exitCode = Run(
                    ["storage", "gc", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--confirm", "--json"],
                    output,
                    fixture.ConfigDirectory);

                Assert.AreEqual(ExitCodes.GeneralError, exitCode, output.ToString());
                Assert.IsGreaterThan(0, locked.Length);
            }

            using var document = JsonDocument.Parse(output.ToString());
            var data = document.RootElement.GetProperty("data");
            Assert.AreEqual("error", data.GetProperty("status").GetString());
            var recoveryPoint = data.GetProperty("recoveryPointPath").GetString();
            Assert.IsNotNull(recoveryPoint);
            Assert.IsTrue(File.Exists(Path.Combine(
                recoveryPoint,
                "blobs",
                ordered[0].BlobId + ".blob")));
            Assert.IsTrue(File.Exists(ordered[1].Path));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    public async Task StorageGc_IncompleteReferenceScan_BlocksAllCandidates()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var orphan = await CreateBlobFileAsync(fixture, "blocked orphan", ageDays: 10);
            await File.AppendAllTextAsync(
                fixture.MessagePath,
                "{\"incomplete\":",
                Encoding.UTF8,
                TestContext.CancellationToken);
            var before = await CaptureTreeAsync(fixture.Root);
            var output = new StringWriter();

            var exitCode = Run(
                ["storage", "gc", "--workspace", fixture.Workspace, "--data-dir", fixture.DataDirectory, "--confirm", "--json"],
                output,
                fixture.ConfigDirectory);

            Assert.AreEqual(ExitCodes.GeneralError, exitCode, output.ToString());
            using var document = JsonDocument.Parse(output.ToString());
            var data = document.RootElement.GetProperty("data");
            Assert.AreEqual("blocked", data.GetProperty("status").GetString());
            Assert.AreEqual(0, data.GetProperty("candidateCount").GetInt32());
            Assert.IsTrue(File.Exists(orphan.Path));
            AssertTreeEqual(before, await CaptureTreeAsync(fixture.Root));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    private async Task<StorageFixture> CreateFixtureAsync()
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
                "storage-check-session",
                TimeSpan.FromHours(1),
                TestContext.CancellationToken);
        }

        var store = new ConversationStore(dataDirectory);
        var messageBlob = await store.BlobStore.StoreAsync(
            Encoding.UTF8.GetBytes("canonical message blob"),
            "text/plain",
            "session",
            ct: TestContext.CancellationToken);
        var messageReference = new BlobReference(
            messageBlob.BlobId,
            messageBlob.Length,
            messageBlob.Sha256,
            messageBlob.ContentType,
            messageBlob.AccessScope,
            messageBlob.ExpiresAt);
        var content = JsonSerializer.SerializeToElement<ContentBlock[]>(
            [new BlobRefContentBlock(messageReference)],
            RuntimeJsonContext.Default.ContentBlockArray);
        await store.AppendMessageAsync(
            sessionId,
            "Expert",
            new ConversationMessageDraft(
                "invocation-storage-check",
                "agent-storage-check",
                "user",
                content,
                DateTimeOffset.UtcNow),
            TestContext.CancellationToken);

        var toolBlob = await store.BlobStore.StoreAsync(
            Encoding.UTF8.GetBytes("tool result blob"),
            "application/octet-stream",
            "session",
            ct: TestContext.CancellationToken);
        await InsertToolBlobReferenceAsync(dataDirectory, sessionId, toolBlob);

        return new StorageFixture(
            root,
            workspace,
            dataDirectory,
            configDirectory,
            sessionId,
            Path.Combine(dataDirectory, "messages", sessionId + ".jsonl"),
            messageBlob.BlobId,
            Path.Combine(dataDirectory, "blobs", messageBlob.BlobId + ".blob"),
            toolBlob.BlobId,
            Path.Combine(dataDirectory, "blobs", toolBlob.BlobId + ".blob"));
    }

    private async Task InsertToolBlobReferenceAsync(
        string dataDirectory,
        string sessionId,
        ConversationBlobReference blob)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(dataDirectory, "state.db")};Pooling=False");
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tool_intents(
                call_id,
                session_id,
                result_blob_id,
                result_blob_length,
                result_blob_sha256,
                result_blob_content_type,
                result_blob_access_scope,
                result_blob_expires_at)
            VALUES(
                'storage-tool-call',
                $sessionId,
                $blobId,
                $length,
                $sha256,
                $contentType,
                $accessScope,
                $expiresAt);
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$blobId", blob.BlobId);
        command.Parameters.AddWithValue("$length", blob.Length);
        command.Parameters.AddWithValue("$sha256", blob.Sha256);
        command.Parameters.AddWithValue("$contentType", blob.ContentType);
        command.Parameters.AddWithValue("$accessScope", blob.AccessScope);
        command.Parameters.AddWithValue("$expiresAt", blob.ExpiresAt.ToString("O"));
        await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
    }

    private async Task<BlobFile> CreateBlobFileAsync(
        StorageFixture fixture,
        string content,
        int ageDays)
    {
        var bytes = Encoding.UTF8.GetBytes(content + Guid.NewGuid().ToString("N"));
        var blobId = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var path = Path.Combine(fixture.DataDirectory, "blobs", blobId + ".blob");
        await File.WriteAllBytesAsync(path, bytes, TestContext.CancellationToken);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
        return new BlobFile(blobId, path, bytes.LongLength);
    }

    private static int Run(string[] args, TextWriter output, string configDirectory) =>
        CliApplication.RunForTests(
            args,
            output,
            new StringReader(string.Empty),
            configDirectory,
            static _ => static _ => throw new InvalidOperationException(
                "Provider resolution was not expected."));

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
            await using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            var hash = Convert.ToHexString(
                await SHA256.HashDataAsync(stream));
            var info = new FileInfo(path);
            files.Add(new FileSnapshot(
                Path.GetRelativePath(root, path),
                info.Length,
                hash,
                info.LastWriteTimeUtc));
        }

        return new TreeSnapshot(directories, [.. files]);
    }

    private static void AssertTreeEqual(TreeSnapshot expected, TreeSnapshot actual)
    {
        CollectionAssert.AreEqual(expected.Directories, actual.Directories);
        CollectionAssert.AreEqual(expected.Files, actual.Files);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-stage7-storage-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private sealed record StorageFixture(
        string Root,
        string Workspace,
        string DataDirectory,
        string ConfigDirectory,
        string SessionId,
        string MessagePath,
        string MessageBlobId,
        string MessageBlobPath,
        string ToolBlobId,
        string ToolBlobPath);

    private sealed record BlobFile(string BlobId, string Path, long Length);

    private sealed record TreeSnapshot(string[] Directories, FileSnapshot[] Files);

    private sealed record FileSnapshot(
        string RelativePath,
        long Length,
        string Sha256,
        DateTime LastWriteTimeUtc);
}
