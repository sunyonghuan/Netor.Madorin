using System.Security.Cryptography;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Server;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class RuntimeLifecycleTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    public async Task WorkspaceAndInstanceLocks_AreAtomicAndIndependent()
    {
        var root = CreateTemporaryDirectory();
        var workspaceA = Path.Combine(root, "a");
        var workspaceB = Path.Combine(root, "b");
        var instanceA = Guid.NewGuid().ToString("N");
        var instanceB = Guid.NewGuid().ToString("N");
        var prefix = $"madorin.lifecycle.{Guid.NewGuid():N}";
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        try
        {
            await using var first = await RuntimeServer.StartAsync(
                CreateOptions(workspaceA, instanceA, prefix, secret));

            var workspaceConflict = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await RuntimeServer.StartAsync(
                    CreateOptions(workspaceA, instanceB, prefix + ".other", secret)));
            StringAssert.Contains(workspaceConflict.Message, "workspace");

            var instanceConflict = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await RuntimeServer.StartAsync(
                    CreateOptions(workspaceB, instanceA, prefix, secret)));
            StringAssert.Contains(instanceConflict.Message, "instance name");

            await using var independent = await RuntimeServer.StartAsync(
                CreateOptions(workspaceB, instanceB, prefix + ".other", secret));
            Assert.AreNotEqual(first.PipeName, independent.PipeName);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task StartAsync_AfterForcedStop_InterruptsActiveRunAndPreservesTerminalRun()
    {
        var workspace = CreateTemporaryDirectory();
        var dataDirectory = Path.Combine(workspace, ".madorin");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string runningRunId;
        string completedRunId;
        try
        {
            await using (var seedConnection = await DataDirectoryInitializer.InitializeAsync(
                             dataDirectory,
                             TestContext.CancellationToken))
            {
                var repository = new SqliteSessionRepository(seedConnection);
                var sessionId = await repository.CreateSessionAsync(
                    RuntimeMode.Expert,
                    "recovery-session",
                    TimeSpan.FromDays(1),
                    TestContext.CancellationToken);
                runningRunId = await repository.CreateRunAsync(
                    sessionId,
                    "recovery-running",
                    TimeSpan.FromDays(1),
                    TestContext.CancellationToken);
                completedRunId = await repository.CreateRunAsync(
                    sessionId,
                    "recovery-completed",
                    TimeSpan.FromDays(1),
                    TestContext.CancellationToken);
                await TransitionToRunningAsync(repository, runningRunId);
                await TransitionToRunningAsync(repository, completedRunId);
                await repository.TransitionRunStatusAsync(
                    completedRunId,
                    RunStatus.Running,
                    RunStatus.Persisting,
                    TestContext.CancellationToken);
                await repository.TransitionRunToTerminalAsync(
                    completedRunId,
                    RunStatus.Persisting,
                    RunStatus.Completed,
                    "completed before restart",
                    TestContext.CancellationToken);
            }

            await using var server = await RuntimeServer.StartAsync(
                CreateOptions(
                    workspace,
                    Guid.NewGuid().ToString("N"),
                    $"madorin.recovery.{Guid.NewGuid():N}",
                    secret),
                TestContext.CancellationToken);
            await using var verificationConnection = new SqliteConnection(
                $"Data Source={Path.Combine(dataDirectory, "state.db")};Pooling=False");
            await verificationConnection.OpenAsync(TestContext.CancellationToken);
            var verificationRepository = new SqliteSessionRepository(verificationConnection);

            Assert.AreEqual(
                RunStatus.Interrupted,
                await verificationRepository.GetRunStatusAsync(
                    runningRunId,
                    TestContext.CancellationToken));
            var completed = await verificationRepository.GetRunSnapshotAsync(
                completedRunId,
                TestContext.CancellationToken);
            Assert.IsNotNull(completed);
            Assert.AreEqual(RunStatus.Completed, completed.Status);
            Assert.AreEqual("completed before restart", completed.TerminalText);
        }
        finally
        {
            TryDelete(workspace);
        }

        async Task TransitionToRunningAsync(
            SqliteSessionRepository repository,
            string runId)
        {
            await repository.TransitionRunStatusAsync(
                runId,
                RunStatus.Accepted,
                RunStatus.Preparing,
                TestContext.CancellationToken);
            await repository.TransitionRunStatusAsync(
                runId,
                RunStatus.Preparing,
                RunStatus.Running,
                TestContext.CancellationToken);
        }
    }

    private static RuntimeServerOptions CreateOptions(
        string workspace,
        string instanceId,
        string pipePrefix,
        string secret) =>
        new(workspace)
        {
            InstanceId = instanceId,
            PipePrefix = pipePrefix,
            HandshakeSecret = secret
        };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-runtime-lifecycle-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
