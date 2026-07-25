using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class SessionListStage7Tests(TestContext testContext)
{
    private const string ProviderId = "session-list-provider";
    private const string ModelId = "session-list-model";

    private static readonly AgentRef ExpertAgent = new(
        "session-list-expert",
        "v1",
        "Answer as the expert.",
        ProviderId,
        ModelId);
    private static readonly AgentRef MeetingAgentA = new(
        "session-list-meeting-a",
        "v1",
        "Represent viewpoint A.",
        ProviderId,
        ModelId);
    private static readonly AgentRef MeetingAgentB = new(
        "session-list-meeting-b",
        "v1",
        "Represent viewpoint B.",
        ProviderId,
        ModelId);
    private static readonly AgentRef WorkManager = new(
        "session-list-work-manager",
        "v1",
        "Create a one-step plan.",
        ProviderId,
        ModelId);
    private static readonly AgentRef WorkWorker = new(
        "session-list-work-worker",
        "v1",
        "Complete the assigned step.",
        ProviderId,
        ModelId);

    [TestMethod]
    [DoNotParallelize]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ListSessionsAsync_ThreeModes_FiltersPagesAndDerivesTitles()
    {
        await using var fixture = await RuntimeFixture.StartAsync(
            testContext.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            testContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        var workTitle = "Work " + new string('x', 114);

        var expert = await StartAndCompleteAsync(
            fixture.Client,
            RuntimeMode.Expert,
            "expert",
            [
                new TextContentBlock(" \t\r\n\u00a0 "),
                new TextContentBlock(" \tShared\u00a0\r\nExpert title  "),
                new TextContentBlock("must-not-be-appended")
            ],
            timeout.Token);
        var meeting = await StartAndCompleteAsync(
            fixture.Client,
            RuntimeMode.Meeting,
            "meeting",
            [new TextContentBlock("Meeting filter needle")],
            timeout.Token);
        var work = await StartAndCompleteAsync(
            fixture.Client,
            RuntimeMode.Work,
            "work",
            [new TextContentBlock($"  {workTitle}\U0001f600 must disappear")],
            timeout.Token);

        var defaultPage = await fixture.Client.ListSessionsAsync(
            new SessionListParameters(Limit: 10),
            timeout.Token);
        Assert.HasCount(3, defaultPage.Sessions);
        Assert.IsNull(defaultPage.NextCursor);
        Assert.IsTrue(defaultPage.Sessions.All(
            static session => session.Status == SessionStatus.Active));

        var expertItem = Assert.ContainsSingle(defaultPage.Sessions.Where(
            session => session.SessionId == expert.SessionId));
        Assert.AreEqual(RuntimeMode.Expert, expertItem.Mode);
        var expertTitle = expertItem.Title
            ?? throw new InvalidDataException("The Expert session title was not derived.");
        Assert.AreEqual("Shared Expert title", expertTitle);
        Assert.DoesNotContain("must-not-be-appended", expertTitle);

        var meetingItem = Assert.ContainsSingle(defaultPage.Sessions.Where(
            session => session.SessionId == meeting.SessionId));
        Assert.AreEqual(RuntimeMode.Meeting, meetingItem.Mode);
        Assert.AreEqual("Meeting filter needle", meetingItem.Title);

        var workItem = Assert.ContainsSingle(defaultPage.Sessions.Where(
            session => session.SessionId == work.SessionId));
        Assert.AreEqual(RuntimeMode.Work, workItem.Mode);
        var actualWorkTitle = workItem.Title
            ?? throw new InvalidDataException("The Work session title was not derived.");
        Assert.AreEqual(workTitle, actualWorkTitle);
        Assert.AreEqual(119, actualWorkTitle.Length);
        Assert.IsFalse(actualWorkTitle.Any(char.IsSurrogate));

        var filtered = await fixture.Client.ListSessionsAsync(
            new SessionListParameters(
                RuntimeMode.Meeting,
                SessionStatus.Active,
                since,
                Search: "filter needle",
                Limit: 10),
            timeout.Token);
        var filteredItem = Assert.ContainsSingle(filtered.Sessions);
        Assert.AreEqual(meeting.SessionId, filteredItem.SessionId);
        Assert.IsNull(filtered.NextCursor);

        var archived = await fixture.Client.ListSessionsAsync(
            new SessionListParameters(Status: SessionStatus.Archived, Limit: 10),
            timeout.Token);
        Assert.IsEmpty(archived.Sessions);
        Assert.IsNull(archived.NextCursor);

        var firstPage = await fixture.Client.ListSessionsAsync(
            new SessionListParameters(Limit: 2),
            timeout.Token);
        Assert.HasCount(2, firstPage.Sessions);
        Assert.IsNotNull(firstPage.NextCursor);
        var secondPage = await fixture.Client.ListSessionsAsync(
            new SessionListParameters(Limit: 2, Cursor: firstPage.NextCursor),
            timeout.Token);
        var secondPageItem = Assert.ContainsSingle(secondPage.Sessions);
        Assert.IsNull(secondPage.NextCursor);
        Assert.DoesNotContain(
            secondPageItem.SessionId,
            firstPage.Sessions.Select(static session => session.SessionId));

        var pagedSessionIds = firstPage.Sessions
            .Concat(secondPage.Sessions)
            .Select(static session => session.SessionId)
            .ToArray();
        Assert.HasCount(3, pagedSessionIds);
        Assert.AreEqual(
            3,
            pagedSessionIds.Distinct(StringComparer.Ordinal).Count());

        var empty = await fixture.Client.ListSessionsAsync(
            new SessionListParameters(
                RuntimeMode.Expert,
                SessionStatus.Active,
                since,
                Search: "no-matching-session-title",
                Limit: 10),
            timeout.Token);
        Assert.IsEmpty(empty.Sessions);
        Assert.IsNull(empty.NextCursor);
    }

    private static async Task<SessionCapture> StartAndCompleteAsync(
        RuntimeClient client,
        RuntimeMode mode,
        string key,
        ContentBlock[] input,
        CancellationToken cancellationToken)
    {
        var runId = await client.StartNewSessionRunAsync(
            new NewSessionRunRequest(
                $"session-list-{key}-session",
                $"session-list-{key}-run",
                mode,
                CreateSelection(mode),
                input),
            cancellationToken);
        var terminal = await ReadTerminalEventAsync(
            client,
            runId,
            cancellationToken);
        Assert.AreEqual(
            MessageTypes.RunCompleted,
            terminal.MessageType,
            terminal.Payload.GetRawText());
        await client.AcknowledgeEventsAsync(terminal.Gsn, cancellationToken);
        var run = await client.QueryRunAsync(runId, cancellationToken);
        return new SessionCapture(run.SessionId, runId);
    }

    private static NextTurnSelection CreateSelection(RuntimeMode mode)
    {
        ModeOptions modeOptions = mode switch
        {
            RuntimeMode.Expert => new ExpertModeOptions(ExpertAgent),
            RuntimeMode.Meeting => new MeetingModeOptions(
            [
                new MeetingParticipant(
                    "session-list-participant-a",
                    MeetingAgentA,
                    "A",
                    JoinOrder: 0),
                new MeetingParticipant(
                    "session-list-participant-b",
                    MeetingAgentB,
                    "B",
                    JoinOrder: 1)
            ]),
            RuntimeMode.Work => new WorkModeOptions(
                WorkManager,
                [WorkWorker],
                WorkflowPolicy.Default),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
        return new NextTurnSelection(
            1,
            mode,
            new DefaultSelection(ProviderId, ModelId),
            modeOptions);
    }

    private static async Task<RuntimeEventEnvelope> ReadTerminalEventAsync(
        RuntimeClient client,
        string runId,
        CancellationToken cancellationToken)
    {
        await foreach (var envelope in client.ReadEventsAsync(0, cancellationToken))
        {
            if (envelope.RunId == runId
                && envelope.MessageType is MessageTypes.RunCompleted
                    or MessageTypes.RunFailed
                    or MessageTypes.RunCancelled)
            {
                return envelope;
            }
        }

        throw new InvalidOperationException($"Run '{runId}' did not emit a terminal event.");
    }

    private sealed record SessionCapture(string SessionId, string RunId);

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly string _workspace;
        private readonly CancellationTokenSource _shutdown;
        private readonly Task _serverTask;

        private RuntimeFixture(
            string workspace,
            RuntimeServer server,
            RuntimeClient client,
            CancellationTokenSource shutdown,
            Task serverTask)
        {
            _workspace = workspace;
            Server = server;
            Client = client;
            _shutdown = shutdown;
            _serverTask = serverTask;
        }

        public RuntimeServer Server { get; }

        public RuntimeClient Client { get; }

        public static async Task<RuntimeFixture> StartAsync(
            CancellationToken cancellationToken)
        {
            var workspace = Path.Combine(
                Path.GetTempPath(),
                "madorin-session-list-stage7-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = Guid.NewGuid().ToString("N"),
                    PipePrefix = $"madorin.session-list-stage7.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = _ => new DeterministicProvider()
                },
                cancellationToken);
            var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: server.InstanceId,
                    HandshakeSecret: secret,
                    EnableBackgroundHeartbeat: false));
            try
            {
                await client.ConnectAsync(cancellationToken);
                return new RuntimeFixture(
                    workspace,
                    server,
                    client,
                    shutdown,
                    serverTask);
            }
            catch
            {
                await client.DisposeAsync();
                shutdown.Cancel();
                await serverTask;
                await server.DisposeAsync();
                shutdown.Dispose();
                DeleteDirectory(workspace);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            _shutdown.Cancel();
            await _serverTask;
            await Server.DisposeAsync();
            _shutdown.Dispose();
            DeleteDirectory(_workspace);
        }

        private static void DeleteDirectory(string path)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class DeterministicProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => SessionListStage7Tests.ProviderId;

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new(ModelId, "Session List Model", ContextWindow: 8_192)];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            var text = string.Equals(
                request.AgentId,
                WorkManager.AgentId,
                StringComparison.Ordinal)
                ? """
                  {"planVersion":"1","goal":"List sessions","steps":[{"stepId":"list-step","goal":"Produce a deterministic result","targetAgentId":"session-list-work-worker","dependsOn":[],"depth":0}]}
                  """
                : $"result:{request.AgentId}";
            yield return new TextDeltaProviderEvent(request.InvocationId, text);
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
