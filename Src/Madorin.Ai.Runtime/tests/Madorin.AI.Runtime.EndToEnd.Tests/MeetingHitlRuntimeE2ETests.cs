using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class MeetingHitlRuntimeE2ETests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MeetingHitl_RequestResumeApproveDuplicate_CompletesSuccessfully()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var capturedRequests = new ConcurrentBag<RuntimeProviderRequest>();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);

        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.meeting-hitl-e2e.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = selection =>
                        new RecordingCompletingProvider(
                            capturedRequests,
                            selection.DefaultSelection.ProviderId)
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);

            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await client.ConnectAsync(timeout.Token);

            var participants = new[]
            {
                new MeetingParticipant(
                    "participant-a",
                    new AgentRef("agent-a", "v1", "Agent A",
                        ProviderId: "hitl-provider-a",
                        ModelId: "hitl-model-a"),
                    "Agent A",
                    JoinOrder: 0),
                new MeetingParticipant(
                    "participant-b",
                    new AgentRef("agent-b", "v1", "Agent B",
                        ProviderId: "hitl-provider-b",
                        ModelId: "hitl-model-b"),
                    "Agent B",
                    JoinOrder: 1),
            };

            var meetingOptions = new MeetingModeOptions(
                participants,
                selectorPolicy: new MeetingSelectorPolicyOptions(MaxRounds: 1),
                policy: new MeetingPolicy(HitlEnabled: true, HitlTimeoutSeconds: 20));

            var runId = await client.StartNewSessionRunAsync(
                new NewSessionRunRequest(
                    "hitl-meeting-session",
                    "hitl-meeting-run",
                    RuntimeMode.Meeting,
                    new NextTurnSelection(
                        1,
                        RuntimeMode.Meeting,
                        new DefaultSelection("hitl-primary", "hitl-model"),
                        meetingOptions),
                    [new TextContentBlock("start meeting")]),
                timeout.Token);

            // Step 3: Read the meeting.hitl.request event
            RuntimeEventEnvelope? hitlEvent = null;
            await foreach (var envelope in client.ReadEventsAsync(0, timeout.Token))
            {
                if (envelope.RunId == runId
                    && string.Equals(envelope.MessageType, MessageTypes.MeetingHitlRequest, StringComparison.Ordinal))
                {
                    hitlEvent = envelope;
                    break;
                }
            }

            Assert.IsNotNull(hitlEvent, "Expected a meeting.hitl.request event.");
            var hitlRequest = JsonSerializer.Deserialize(
                hitlEvent!.Payload,
                RuntimeJsonContext.Default.MeetingHitlRequest);
            Assert.IsNotNull(hitlRequest);
            Assert.IsFalse(string.IsNullOrWhiteSpace(hitlRequest.ApprovalRequestId));
            Assert.IsFalse(string.IsNullOrWhiteSpace(hitlRequest.SessionId));
            Assert.IsFalse(string.IsNullOrWhiteSpace(hitlRequest.RunId));

            var sessionId = hitlRequest.SessionId;

            // Step 4: ResumeSessionAsync before responding
            var resumeBefore = await client.ResumeSessionAsync(sessionId, timeout.Token);
            Assert.IsNotNull(resumeBefore.MeetingState);
            Assert.AreEqual(MeetingSessionStatus.WaitingForApproval, resumeBefore.MeetingState.Status);
            Assert.IsNotNull(resumeBefore.MeetingState.PendingApproval);
            Assert.AreEqual(
                hitlRequest.ApprovalRequestId,
                resumeBefore.MeetingState.PendingApproval.ApprovalRequestId);

            // Step 5: Send meeting.hitl.response with Approve
            var approvalResponse = new MeetingHitlResponse(
                hitlRequest.ApprovalRequestId,
                MeetingHitlAction.Approve);
            var responseJson = JsonSerializer.SerializeToElement(
                approvalResponse,
                RuntimeJsonContext.Default.MeetingHitlResponse);
            var rpcResult = await client.ControlPeer.SendRequestAsync(
                MessageTypes.MeetingHitlResponse,
                responseJson,
                timeout.Token);
            var firstPayload = rpcResult.Result ?? throw new InvalidDataException("Expected a non-null RPC result for the first HITL response.");
            var firstResult = JsonSerializer.Deserialize(
                firstPayload,
                RuntimeJsonContext.Default.Boolean);
            Assert.IsTrue(firstResult);

            // Send the exact same request again (idempotency)
            var duplicateResult = await client.ControlPeer.SendRequestAsync(
                MessageTypes.MeetingHitlResponse,
                responseJson,
                timeout.Token);
            var duplicatePayload = duplicateResult.Result ?? throw new InvalidDataException("Expected a non-null RPC result for the duplicate HITL response.");
            var secondResult = JsonSerializer.Deserialize(
                duplicatePayload,
                RuntimeJsonContext.Default.Boolean);
            Assert.IsTrue(secondResult);

            // Step 6: Read run.completed event
            RuntimeEventEnvelope? completedEvent = null;
            MeetingHitlResponse? persistedResponse = null;
            var responseEventCount = 0;
            await foreach (var envelope in client.ReadEventsAsync(0, timeout.Token))
            {
                if (envelope.RunId != runId)
                {
                    continue;
                }

                if (string.Equals(
                    envelope.MessageType,
                    MessageTypes.MeetingHitlResponse,
                    StringComparison.Ordinal))
                {
                    responseEventCount++;
                    persistedResponse = JsonSerializer.Deserialize(
                        envelope.Payload,
                        RuntimeJsonContext.Default.MeetingHitlResponse);
                }

                if (string.Equals(
                    envelope.MessageType,
                    MessageTypes.RunCompleted,
                    StringComparison.Ordinal))
                {
                    completedEvent = envelope;
                    break;
                }
            }

            Assert.IsNotNull(completedEvent, "Expected a run.completed event.");
            Assert.AreEqual(1, responseEventCount);
            Assert.IsNotNull(persistedResponse);
            Assert.AreEqual(
                hitlRequest.ApprovalRequestId,
                persistedResponse.ApprovalRequestId);
            Assert.AreEqual(MeetingHitlAction.Approve, persistedResponse.Action);
            Assert.IsNotEmpty(capturedRequests, "Expected the fake provider to be invoked.");

            // Resume again and assert terminal state with no pending approval
            var resumeAfter = await client.ResumeSessionAsync(sessionId, timeout.Token);
            Assert.IsNotNull(resumeAfter.MeetingState);
            Assert.IsTrue(
                resumeAfter.MeetingState.Status == MeetingSessionStatus.Completed
                || resumeAfter.MeetingState.Status == MeetingSessionStatus.Failed
                || resumeAfter.MeetingState.Status == MeetingSessionStatus.Cancelled,
                $"Expected a terminal meeting status but got {resumeAfter.MeetingState.Status}.");
            Assert.IsNull(resumeAfter.MeetingState.PendingApproval);

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MeetingHitl_AfterRuntimeRestart_RehydratesAndCompletesOriginalRunOnce()
    {
        var workspace = CreateTemporaryDirectory();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var capturedRequests = new ConcurrentBag<RuntimeProviderRequest>();
        var providerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstShutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        using var secondShutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);

        var participants = new[]
        {
            new MeetingParticipant(
                "participant-a",
                new AgentRef("agent-a", "v1", "Agent A",
                    ProviderId: "restart-provider-a",
                    ModelId: "restart-model-a"),
                "Agent A",
                JoinOrder: 0),
            new MeetingParticipant(
                "participant-b",
                new AgentRef("agent-b", "v1", "Agent B",
                    ProviderId: "restart-provider-b",
                    ModelId: "restart-model-b"),
                "Agent B",
                JoinOrder: 1),
        };
        var meetingOptions = new MeetingModeOptions(
            participants,
            selectorPolicy: new MeetingSelectorPolicyOptions(MaxRounds: 1),
            policy: new MeetingPolicy(HitlEnabled: true, HitlTimeoutSeconds: 20));

        try
        {
            string runId;
            MeetingHitlRequest hitlRequest;
            var firstInstanceId = Guid.NewGuid().ToString("N");
            await using (var firstServer = await RuntimeServer.StartAsync(
                             new RuntimeServerOptions(workspace)
                             {
                                 InstanceId = firstInstanceId,
                                 PipePrefix = $"madorin.meeting-hitl-restart.first.{Guid.NewGuid():N}",
                                 HandshakeSecret = secret,
                                 MemoryUserHome = Path.Combine(workspace, "home"),
                                 ProviderResolver = selection =>
                                     new RecordingCompletingProvider(
                                         capturedRequests,
                                         selection.DefaultSelection.ProviderId)
                             },
                             TestContext.CancellationToken))
            {
                var firstServerTask = firstServer.RunAsync(firstShutdown.Token);
                await using var firstClient = new RuntimeClient(
                    new RuntimeClientOptions(
                        firstServer.PipeName,
                        Guid.NewGuid().ToString("N"),
                        ExpectedRuntimeInstanceId: firstInstanceId,
                        HandshakeSecret: secret));
                using var firstTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    TestContext.CancellationToken);
                firstTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                await firstClient.ConnectAsync(firstTimeout.Token);

                runId = await firstClient.StartNewSessionRunAsync(
                    new NewSessionRunRequest(
                        "hitl-restart-session",
                        "hitl-restart-run",
                        RuntimeMode.Meeting,
                        new NextTurnSelection(
                            1,
                            RuntimeMode.Meeting,
                            new DefaultSelection("restart-primary", "restart-model"),
                            meetingOptions),
                        [new TextContentBlock("start restartable meeting")]),
                    firstTimeout.Token);

                MeetingHitlRequest? pendingRequest = null;
                await foreach (var envelope in firstClient.ReadEventsAsync(0, firstTimeout.Token))
                {
                    if (envelope.RunId == runId
                        && envelope.MessageType == MessageTypes.MeetingHitlRequest)
                    {
                        pendingRequest = JsonSerializer.Deserialize(
                            envelope.Payload,
                            RuntimeJsonContext.Default.MeetingHitlRequest);
                        break;
                    }
                }

                hitlRequest = pendingRequest
                    ?? throw new AssertFailedException("Expected a meeting HITL request before restart.");
                var beforeRestart = await firstClient.ResumeSessionAsync(
                    hitlRequest.SessionId,
                    firstTimeout.Token);
                Assert.IsNotNull(beforeRestart.MeetingState);
                Assert.AreEqual(
                    MeetingSessionStatus.WaitingForApproval,
                    beforeRestart.MeetingState.Status);
                Assert.AreEqual(
                    hitlRequest.ApprovalRequestId,
                    beforeRestart.MeetingState.PendingApproval?.ApprovalRequestId);

                firstShutdown.Cancel();
                await firstServerTask;
            }

            var secondInstanceId = Guid.NewGuid().ToString("N");
            await using (var secondServer = await RuntimeServer.StartAsync(
                             new RuntimeServerOptions(workspace)
                             {
                                 InstanceId = secondInstanceId,
                                 PipePrefix = $"madorin.meeting-hitl-restart.second.{Guid.NewGuid():N}",
                                 HandshakeSecret = secret,
                                 MemoryUserHome = Path.Combine(workspace, "home"),
                                 ProviderResolver = selection =>
                                     new BlockingCompletingProvider(
                                         capturedRequests,
                                         providerStarted,
                                         releaseProvider,
                                         selection.DefaultSelection.ProviderId)
                             },
                             TestContext.CancellationToken))
            {
                var secondServerTask = secondServer.RunAsync(secondShutdown.Token);
                await using var secondClient = new RuntimeClient(
                    new RuntimeClientOptions(
                        secondServer.PipeName,
                        Guid.NewGuid().ToString("N"),
                        ExpectedRuntimeInstanceId: secondInstanceId,
                        HandshakeSecret: secret));
                using var secondTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    TestContext.CancellationToken);
                secondTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                await secondClient.ConnectAsync(secondTimeout.Token);

                var resumed = await secondClient.ResumeSessionAsync(
                    hitlRequest.SessionId,
                    secondTimeout.Token);
                Assert.IsNotNull(resumed.MeetingState);
                Assert.AreEqual(
                    MeetingSessionStatus.WaitingForApproval,
                    resumed.MeetingState.Status);
                Assert.AreEqual(
                    hitlRequest.ApprovalRequestId,
                    resumed.MeetingState.PendingApproval?.ApprovalRequestId);
                Assert.AreEqual(runId, resumed.LatestRun?.RunId);
                Assert.HasCount(participants.Length, resumed.NeededDefinitions);

                var rehydrate = await secondClient.RehydrateSessionAsync(
                    new SessionRehydrateParameters(
                        hitlRequest.SessionId,
                        [.. participants.Select(static participant =>
                            new AgentDefinition(participant.Agent, participant.ParticipantId))]),
                    secondTimeout.Token);
                Assert.AreEqual("Ready", rehydrate.Status);
                Assert.IsEmpty(rehydrate.Mismatched);

                var approvalResponse = new MeetingHitlResponse(
                    hitlRequest.ApprovalRequestId,
                    MeetingHitlAction.Approve);
                var responseJson = JsonSerializer.SerializeToElement(
                    approvalResponse,
                    RuntimeJsonContext.Default.MeetingHitlResponse);
                var firstRpcResponse = await secondClient.ControlPeer.SendRequestAsync(
                    MessageTypes.MeetingHitlResponse,
                    responseJson,
                    secondTimeout.Token);
                Assert.IsTrue(JsonSerializer.Deserialize(
                    firstRpcResponse.Result
                        ?? throw new InvalidDataException("Expected the first resumed HITL result."),
                    RuntimeJsonContext.Default.Boolean));

                await providerStarted.Task.WaitAsync(secondTimeout.Token);
                var duplicateRpcResponse = await secondClient.ControlPeer.SendRequestAsync(
                    MessageTypes.MeetingHitlResponse,
                    responseJson,
                    secondTimeout.Token);
                Assert.IsTrue(JsonSerializer.Deserialize(
                    duplicateRpcResponse.Result
                        ?? throw new InvalidDataException("Expected the duplicate resumed HITL result."),
                    RuntimeJsonContext.Default.Boolean));
                releaseProvider.TrySetResult();

                var requestEventCount = 0;
                var responseEventCount = 0;
                RuntimeEventEnvelope? completed = null;
                await foreach (var envelope in secondClient.ReadEventsAsync(0, secondTimeout.Token))
                {
                    if (envelope.RunId != runId)
                    {
                        continue;
                    }

                    if (envelope.MessageType == MessageTypes.MeetingHitlRequest)
                    {
                        requestEventCount++;
                    }
                    else if (envelope.MessageType == MessageTypes.MeetingHitlResponse)
                    {
                        responseEventCount++;
                    }
                    else if (envelope.MessageType == MessageTypes.RunCompleted)
                    {
                        completed = envelope;
                        break;
                    }
                }

                Assert.IsNotNull(completed);
                Assert.AreEqual(1, requestEventCount);
                Assert.AreEqual(1, responseEventCount);
                Assert.IsNotEmpty(capturedRequests);
                foreach (var providerRequest in capturedRequests)
                {
                    Assert.AreEqual(
                        1,
                        providerRequest.Messages
                            .SelectMany(static message => message.Content)
                            .OfType<TextContentBlock>()
                            .Count(static block =>
                                block.Text == "start restartable meeting"));
                }

                var afterCompletion = await secondClient.ResumeSessionAsync(
                    hitlRequest.SessionId,
                    secondTimeout.Token);
                Assert.IsNotNull(afterCompletion.MeetingState);
                Assert.AreEqual(
                    MeetingSessionStatus.Completed,
                    afterCompletion.MeetingState.Status);
                Assert.IsNull(afterCompletion.MeetingState.PendingApproval);
                Assert.AreEqual(runId, afterCompletion.LatestRun?.RunId);

                var messages = await secondClient.ListSessionMessagesAsync(
                    new SessionMessagesListParameters(
                        hitlRequest.SessionId,
                        PageSize: 100),
                    secondTimeout.Token);
                Assert.AreEqual(
                    1,
                    messages.Messages.Count(static message =>
                        message.AgentId == "meeting.user"
                        && message.Role == "user"));

                secondShutdown.Cancel();
                await secondServerTask;
            }
        }
        finally
        {
            releaseProvider.TrySetResult();
            firstShutdown.Cancel();
            secondShutdown.Cancel();
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MeetingInvocation_AfterRuntimeRestart_RehydrateAutomaticallyContinuesOriginalRun()
    {
        var workspace = CreateTemporaryDirectory();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var capturedRequests = new ConcurrentBag<RuntimeProviderRequest>();
        var firstProviderStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstProviderRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var participants = new[]
        {
            new MeetingParticipant(
                "participant-a",
                new AgentRef(
                    "agent-a",
                    "v1",
                    "Agent A",
                    ProviderId: "recovery-provider-a",
                    ModelId: "recovery-model-a"),
                "Agent A",
                JoinOrder: 0),
            new MeetingParticipant(
                "participant-b",
                new AgentRef(
                    "agent-b",
                    "v1",
                    "Agent B",
                    ProviderId: "recovery-provider-b",
                    ModelId: "recovery-model-b"),
                "Agent B",
                JoinOrder: 1),
        };
        var meetingOptions = new MeetingModeOptions(
            participants,
            selectorPolicy: new MeetingSelectorPolicyOptions(
                Type: MeetingSelectorPolicy.RoundRobin,
                MaxRounds: 1));
        string runId;
        string sessionId;

        try
        {
            using (var firstShutdown = CancellationTokenSource.CreateLinkedTokenSource(
                       TestContext.CancellationToken))
            {
                var firstInstanceId = Guid.NewGuid().ToString("N");
                await using var firstServer = await RuntimeServer.StartAsync(
                    new RuntimeServerOptions(workspace)
                    {
                        InstanceId = firstInstanceId,
                        PipePrefix = $"madorin.meeting-recovery.first.{Guid.NewGuid():N}",
                        HandshakeSecret = secret,
                        MemoryUserHome = Path.Combine(workspace, "home"),
                        ProviderResolver = selection => new BlockingCompletingProvider(
                            capturedRequests,
                            firstProviderStarted,
                            firstProviderRelease,
                            selection.DefaultSelection.ProviderId)
                    },
                    TestContext.CancellationToken);
                var firstServerTask = firstServer.RunAsync(firstShutdown.Token);
                await using var firstClient = new RuntimeClient(
                    new RuntimeClientOptions(
                        firstServer.PipeName,
                        Guid.NewGuid().ToString("N"),
                        ExpectedRuntimeInstanceId: firstInstanceId,
                        HandshakeSecret: secret));
                using var firstTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    TestContext.CancellationToken);
                firstTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await firstClient.ConnectAsync(firstTimeout.Token);

                runId = await firstClient.StartNewSessionRunAsync(
                    new NewSessionRunRequest(
                        "invocation-recovery-session",
                        "invocation-recovery-run",
                        RuntimeMode.Meeting,
                        new NextTurnSelection(
                            1,
                            RuntimeMode.Meeting,
                            new DefaultSelection("recovery-primary", "recovery-model"),
                            meetingOptions),
                        [new TextContentBlock("resume the interrupted meeting")]),
                    firstTimeout.Token);
                await firstProviderStarted.Task.WaitAsync(firstTimeout.Token);
                sessionId = (await firstClient.QueryRunAsync(runId, firstTimeout.Token)).SessionId;

                firstShutdown.Cancel();
                await firstServerTask;
            }

            using var secondShutdown = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            var secondInstanceId = Guid.NewGuid().ToString("N");
            await using var secondServer = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = secondInstanceId,
                    PipePrefix = $"madorin.meeting-recovery.second.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = selection => new RecordingCompletingProvider(
                        capturedRequests,
                        selection.DefaultSelection.ProviderId)
                },
                TestContext.CancellationToken);
            var secondServerTask = secondServer.RunAsync(secondShutdown.Token);
            await using var secondClient = new RuntimeClient(
                new RuntimeClientOptions(
                    secondServer.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: secondInstanceId,
                    HandshakeSecret: secret));
            using var secondTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            secondTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await secondClient.ConnectAsync(secondTimeout.Token);

            var beforeRehydrate = await secondClient.ResumeSessionAsync(
                sessionId,
                secondTimeout.Token);
            Assert.IsNotNull(beforeRehydrate.LatestRun);
            Assert.AreEqual(runId, beforeRehydrate.LatestRun.RunId);
            Assert.AreEqual(RunStatus.Interrupted, beforeRehydrate.LatestRun.Status);
            Assert.IsNotNull(beforeRehydrate.MeetingState);
            Assert.AreEqual(
                MeetingInvocationStatus.Interrupted,
                beforeRehydrate.MeetingState.NextScheduledInvocation?.Status);

            var rehydrate = await secondClient.RehydrateSessionAsync(
                new SessionRehydrateParameters(
                    sessionId,
                    [.. participants.Select(static participant =>
                        new AgentDefinition(participant.Agent, participant.ParticipantId))]),
                secondTimeout.Token);
            Assert.AreEqual("Ready", rehydrate.Status);
            Assert.IsEmpty(rehydrate.Mismatched);

            RuntimeEventEnvelope? terminal = null;
            await foreach (var envelope in secondClient.ReadEventsAsync(0, secondTimeout.Token))
            {
                if (envelope.RunId == runId
                    && envelope.MessageType is MessageTypes.RunCompleted
                        or MessageTypes.RunFailed
                        or MessageTypes.RunCancelled)
                {
                    terminal = envelope;
                    break;
                }
            }

            Assert.IsNotNull(terminal);
            Assert.AreEqual(
                MessageTypes.RunCompleted,
                terminal.MessageType,
                terminal.Payload.GetRawText());
            Assert.HasCount(2, capturedRequests);
            Assert.AreEqual(
                1,
                capturedRequests.Select(static request => request.InvocationId).Distinct().Count());
            var afterCompletion = await secondClient.ResumeSessionAsync(
                sessionId,
                secondTimeout.Token);
            Assert.AreEqual(RunStatus.Completed, afterCompletion.LatestRun?.Status);
            Assert.AreEqual(MeetingSessionStatus.Completed, afterCompletion.MeetingState?.Status);
            Assert.IsNull(afterCompletion.MeetingState?.NextScheduledInvocation);

            secondShutdown.Cancel();
            await secondServerTask;
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MeetingSelector_AfterRuntimeRestart_ResumesOriginalDecisionInvocation()
    {
        var selector = new AgentRef(
            "selector-agent",
            "v1",
            "Selector",
            ProviderId: "selector-provider",
            ModelId: "selector-model");
        var participants = CreateRecoveryParticipants();
        var requests = await RunMeetingInvocationRecoveryScenarioAsync(
            participants,
            new MeetingModeOptions(
                participants,
                selectorPolicy: new MeetingSelectorPolicyOptions(
                    Type: MeetingSelectorPolicy.SelectorDriven,
                    SelectorAgent: selector,
                    MaxRounds: 1)),
            request => request.AgentId == selector.AgentId,
            request => request.AgentId == selector.AgentId
                ? """{"participantId":"participant-b"}"""
                : "meeting response");

        var selectorRequests = requests
            .Where(request => request.AgentId == selector.AgentId)
            .ToArray();
        Assert.HasCount(2, selectorRequests);
        Assert.AreEqual(1, selectorRequests.Select(static request => request.InvocationId).Distinct().Count());
        Assert.HasCount(1, requests.Where(static request => request.AgentId == "agent-b"));
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MeetingHost_AfterRuntimeRestart_ResumesOriginalDecisionInvocation()
    {
        var host = new AgentRef(
            "host-agent",
            "v1",
            "Host",
            ProviderId: "host-provider",
            ModelId: "host-model");
        var participants = CreateRecoveryParticipants();
        var requests = await RunMeetingInvocationRecoveryScenarioAsync(
            participants,
            new MeetingModeOptions(
                participants,
                hostAgent: host,
                selectorPolicy: new MeetingSelectorPolicyOptions(
                    Type: MeetingSelectorPolicy.HostDriven,
                    MaxRounds: 1)),
            request => request.AgentId == host.AgentId,
            request => request.AgentId == host.AgentId
                ? """{"conclude":true,"participantId":null}"""
                : "meeting response");

        var hostRequests = requests
            .Where(request => request.AgentId == host.AgentId)
            .ToArray();
        Assert.HasCount(2, hostRequests);
        Assert.AreEqual(1, hostRequests.Select(static request => request.InvocationId).Distinct().Count());
        Assert.IsFalse(requests.Any(static request => request.AgentId is "agent-a" or "agent-b"));
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MeetingParticipant_AfterSelectorCompletedAndRuntimeRestart_ResumesWithoutRepeatingSelector()
    {
        var selector = new AgentRef(
            "selector-agent",
            "v1",
            "Selector",
            ProviderId: "selector-provider",
            ModelId: "selector-model");
        var participants = CreateRecoveryParticipants();
        var requests = await RunMeetingInvocationRecoveryScenarioAsync(
            participants,
            new MeetingModeOptions(
                participants,
                selectorPolicy: new MeetingSelectorPolicyOptions(
                    Type: MeetingSelectorPolicy.SelectorDriven,
                    SelectorAgent: selector,
                    MaxRounds: 1)),
            static request => request.AgentId == "agent-b",
            request => request.AgentId == selector.AgentId
                ? """{"participantId":"participant-b"}"""
                : "meeting response");

        Assert.HasCount(1, requests.Where(request => request.AgentId == selector.AgentId));
        var participantRequests = requests
            .Where(static request => request.AgentId == "agent-b")
            .ToArray();
        Assert.HasCount(2, participantRequests);
        Assert.AreEqual(1, participantRequests.Select(static request => request.InvocationId).Distinct().Count());
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MeetingSummarizer_AfterParticipantCompletedAndRuntimeRestart_ResumesWithoutRepeatingParticipant()
    {
        var summarizer = new AgentRef(
            "summarizer-agent",
            "v1",
            "Summarizer",
            ProviderId: "summarizer-provider",
            ModelId: "summarizer-model");
        var participants = CreateRecoveryParticipants();
        var requests = await RunMeetingInvocationRecoveryScenarioAsync(
            participants,
            new MeetingModeOptions(
                participants,
                selectorPolicy: new MeetingSelectorPolicyOptions(
                    Type: MeetingSelectorPolicy.RoundRobin,
                    MaxRounds: 1),
                policy: new MeetingPolicy(
                    SummaryMode: MeetingSummaryMode.FinalOnly,
                    Summarizer: summarizer)),
            request => request.AgentId == summarizer.AgentId,
            static _ => "meeting response");

        Assert.HasCount(1, requests.Where(static request => request.AgentId == "agent-a"));
        var summarizerRequests = requests
            .Where(request => request.AgentId == summarizer.AgentId)
            .ToArray();
        Assert.HasCount(2, summarizerRequests);
        Assert.AreEqual(1, summarizerRequests.Select(static request => request.InvocationId).Distinct().Count());
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MeetingParticipant_WithCanonicalMessageAfterCrash_ReconcilesWithoutProviderReplay()
    {
        var participants = CreateRecoveryParticipants();
        var requests = await RunMeetingInvocationRecoveryScenarioAsync(
            participants,
            new MeetingModeOptions(
                participants,
                selectorPolicy: new MeetingSelectorPolicyOptions(
                    Type: MeetingSelectorPolicy.RoundRobin,
                    MaxRounds: 1)),
            static request => request.AgentId == "agent-a",
            static _ => "meeting response",
            async (workspace, sessionId, captured, ct) =>
            {
                var interrupted = Assert.ContainsSingle(captured);
                var store = new ConversationStore(workspace);
                var sequence = await store.GetLastSequenceAsync(sessionId, ct) + 1;
                await store.AppendMessageAsync(
                    sessionId,
                    RuntimeMode.Meeting.ToString(),
                    new ConversationRecordV1(
                        "canonical-participant-message",
                        sequence,
                        interrupted.InvocationId,
                        interrupted.AgentId,
                        "assistant",
                        CreateCanonicalTextContent("durable participant response"),
                        DateTimeOffset.UtcNow),
                    ct);
            },
            expectedTerminalText: "durable participant response");

        Assert.HasCount(1, requests);
        Assert.AreEqual("agent-a", requests[0].AgentId);
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MeetingSummarizer_WithCanonicalMessageAfterCrash_ReconcilesWithoutProviderReplay()
    {
        var summarizer = new AgentRef(
            "summarizer-agent",
            "v1",
            "Summarizer",
            ProviderId: "summarizer-provider",
            ModelId: "summarizer-model");
        var participants = CreateRecoveryParticipants();
        var requests = await RunMeetingInvocationRecoveryScenarioAsync(
            participants,
            new MeetingModeOptions(
                participants,
                selectorPolicy: new MeetingSelectorPolicyOptions(
                    Type: MeetingSelectorPolicy.RoundRobin,
                    MaxRounds: 1),
                policy: new MeetingPolicy(
                    SummaryMode: MeetingSummaryMode.FinalOnly,
                    Summarizer: summarizer)),
            request => request.AgentId == summarizer.AgentId,
            static _ => "meeting response",
            async (workspace, sessionId, captured, ct) =>
            {
                var summaryInvocation = Assert.ContainsSingle(
                    captured.Where(request => request.AgentId == summarizer.AgentId));
                var store = new ConversationStore(workspace);
                var summarizesThroughSeq = await store.GetLastSequenceAsync(sessionId, ct);
                await using var connection = await DataDirectoryInitializer.InitializeAsync(workspace, ct);
                var snapshot = await new SqliteMeetingRepository(connection)
                    .GetMeetingSnapshotAsync(sessionId, ct);
                Assert.IsNotNull(snapshot);
                var policyHash = snapshot.Session.PolicyHash;
                using var metadataDocument = JsonDocument.Parse($$"""
                    {
                      "roundIndex": 1,
                      "summarizesThroughSeq": {{summarizesThroughSeq}},
                      "policyHash": "{{policyHash}}",
                      "summarizerAgentId": "{{summarizer.AgentId}}",
                      "summarizerInvocationId": "{{summaryInvocation.InvocationId}}"
                    }
                    """);
                await store.AppendMessageAsync(
                    sessionId,
                    RuntimeMode.Meeting.ToString(),
                    new ConversationRecordV1(
                        "canonical-summary-message",
                        summarizesThroughSeq + 1,
                        summaryInvocation.InvocationId,
                        summaryInvocation.AgentId,
                        "assistant",
                        CreateCanonicalTextContent("durable meeting summary"),
                        DateTimeOffset.UtcNow,
                        SummaryMetadata: metadataDocument.RootElement.Clone()),
                    ct);
            });

        Assert.HasCount(1, requests.Where(static request => request.AgentId == "agent-a"));
        Assert.HasCount(1, requests.Where(request => request.AgentId == summarizer.AgentId));
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MeetingParticipant_WhenRecoveryRetryExhausted_AppliesSkipWithoutThirdProviderCall()
    {
        var participants = CreateRecoveryParticipants();
        var requests = await RunMeetingInvocationRecoveryScenarioAsync(
            participants,
            new MeetingModeOptions(
                participants,
                selectorPolicy: new MeetingSelectorPolicyOptions(
                    Type: MeetingSelectorPolicy.RoundRobin,
                    MaxRounds: 1),
                policy: new MeetingPolicy(
                    MaxRetriesPerInvocation: 1,
                    ParticipantFailure: MeetingParticipantFailurePolicy.Skip)),
            static request => request.AgentId == "agent-a",
            static _ => "meeting response",
            interruptedAttempts: 2);

        var participantRequests = requests
            .Where(static request => request.AgentId == "agent-a")
            .ToArray();
        Assert.HasCount(2, participantRequests);
        Assert.AreEqual(1, participantRequests.Select(static request => request.InvocationId).Distinct().Count());
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MeetingSelector_WhenRecoveryRetryExhausted_UsesFallbackWithoutThirdSelectorCall()
    {
        var selector = new AgentRef(
            "selector-agent",
            "v1",
            "Selector",
            ProviderId: "selector-provider",
            ModelId: "selector-model");
        var participants = CreateRecoveryParticipants();
        var requests = await RunMeetingInvocationRecoveryScenarioAsync(
            participants,
            new MeetingModeOptions(
                participants,
                selectorPolicy: new MeetingSelectorPolicyOptions(
                    Type: MeetingSelectorPolicy.SelectorDriven,
                    SelectorAgent: selector,
                    MaxRounds: 1),
                policy: new MeetingPolicy(
                    MaxRetriesPerInvocation: 1,
                    SelectorFailure: MeetingSelectorFailurePolicy.DeterministicFallback)),
            request => request.AgentId == selector.AgentId,
            static _ => "meeting response",
            interruptedAttempts: 2);

        var selectorRequests = requests
            .Where(request => request.AgentId == selector.AgentId)
            .ToArray();
        Assert.HasCount(2, selectorRequests);
        Assert.AreEqual(1, selectorRequests.Select(static request => request.InvocationId).Distinct().Count());
        Assert.HasCount(1, requests.Where(static request => request.AgentId == "agent-a"));
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MeetingSummarizer_WhenRecoveryRetryExhausted_UsesFallbackWithoutThirdSummarizerCall()
    {
        var summarizer = new AgentRef(
            "summarizer-agent",
            "v1",
            "Summarizer",
            ProviderId: "summarizer-provider",
            ModelId: "summarizer-model");
        var participants = CreateRecoveryParticipants();
        var requests = await RunMeetingInvocationRecoveryScenarioAsync(
            participants,
            new MeetingModeOptions(
                participants,
                selectorPolicy: new MeetingSelectorPolicyOptions(
                    Type: MeetingSelectorPolicy.RoundRobin,
                    MaxRounds: 1),
                policy: new MeetingPolicy(
                    SummaryMode: MeetingSummaryMode.FinalOnly,
                    Summarizer: summarizer,
                    MaxRetriesPerInvocation: 1,
                    SummarizerFailure: MeetingSummarizerFailurePolicy.FallbackTailWindow)),
            request => request.AgentId == summarizer.AgentId,
            static _ => "meeting response",
            interruptedAttempts: 2);

        Assert.HasCount(1, requests.Where(static request => request.AgentId == "agent-a"));
        var summarizerRequests = requests
            .Where(request => request.AgentId == summarizer.AgentId)
            .ToArray();
        Assert.HasCount(2, summarizerRequests);
        Assert.AreEqual(1, summarizerRequests.Select(static request => request.InvocationId).Distinct().Count());
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MeetingHost_WhenRecoveryRetryExhausted_PausesWithoutThirdHostCall()
    {
        var host = new AgentRef(
            "host-agent",
            "v1",
            "Host",
            ProviderId: "host-provider",
            ModelId: "host-model");
        var participants = CreateRecoveryParticipants();
        var requests = await RunMeetingInvocationRecoveryScenarioAsync(
            participants,
            new MeetingModeOptions(
                participants,
                hostAgent: host,
                selectorPolicy: new MeetingSelectorPolicyOptions(
                    Type: MeetingSelectorPolicy.HostDriven,
                    MaxRounds: 1),
                policy: new MeetingPolicy(
                    MaxRetriesPerInvocation: 1,
                    HostFailure: MeetingHostFailurePolicy.Pause)),
            request => request.AgentId == host.AgentId,
            static _ => "meeting response",
            interruptedAttempts: 2,
            expectedRunStatus: RunStatus.WaitingForApproval,
            expectedMeetingStatus: MeetingSessionStatus.Paused);

        var hostRequests = requests
            .Where(request => request.AgentId == host.AgentId)
            .ToArray();
        Assert.HasCount(2, hostRequests);
        Assert.AreEqual(1, hostRequests.Select(static request => request.InvocationId).Distinct().Count());
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MeetingHitl_AfterRuntimeRestart_ExpiresWithoutRehydrate()
    {
        var workspace = CreateTemporaryDirectory();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var capturedRequests = new ConcurrentBag<RuntimeProviderRequest>();
        using var firstShutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        using var secondShutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);

        var participants = new[]
        {
            new MeetingParticipant(
                "participant-a",
                new AgentRef("agent-a", "v1", "Agent A",
                    ProviderId: "timeout-provider-a",
                    ModelId: "timeout-model-a"),
                "Agent A",
                JoinOrder: 0),
            new MeetingParticipant(
                "participant-b",
                new AgentRef("agent-b", "v1", "Agent B",
                    ProviderId: "timeout-provider-b",
                    ModelId: "timeout-model-b"),
                "Agent B",
                JoinOrder: 1),
        };
        var meetingOptions = new MeetingModeOptions(
            participants,
            selectorPolicy: new MeetingSelectorPolicyOptions(MaxRounds: 1),
            policy: new MeetingPolicy(HitlEnabled: true, HitlTimeoutSeconds: 2));

        try
        {
            string runId;
            MeetingHitlRequest hitlRequest;
            var firstInstanceId = Guid.NewGuid().ToString("N");
            await using (var firstServer = await RuntimeServer.StartAsync(
                             new RuntimeServerOptions(workspace)
                             {
                                 InstanceId = firstInstanceId,
                                 PipePrefix = $"madorin.meeting-hitl-timeout.first.{Guid.NewGuid():N}",
                                 HandshakeSecret = secret,
                                 MemoryUserHome = Path.Combine(workspace, "home"),
                                 ProviderResolver = selection =>
                                     new RecordingCompletingProvider(
                                         capturedRequests,
                                         selection.DefaultSelection.ProviderId)
                             },
                             TestContext.CancellationToken))
            {
                var firstServerTask = firstServer.RunAsync(firstShutdown.Token);
                await using var firstClient = new RuntimeClient(
                    new RuntimeClientOptions(
                        firstServer.PipeName,
                        Guid.NewGuid().ToString("N"),
                        ExpectedRuntimeInstanceId: firstInstanceId,
                        HandshakeSecret: secret));
                using var firstTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    TestContext.CancellationToken);
                firstTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                await firstClient.ConnectAsync(firstTimeout.Token);

                runId = await firstClient.StartNewSessionRunAsync(
                    new NewSessionRunRequest(
                        "hitl-timeout-restart-session",
                        "hitl-timeout-restart-run",
                        RuntimeMode.Meeting,
                        new NextTurnSelection(
                            1,
                            RuntimeMode.Meeting,
                            new DefaultSelection("timeout-primary", "timeout-model"),
                            meetingOptions),
                        [new TextContentBlock("wait for approval timeout")]),
                    firstTimeout.Token);

                MeetingHitlRequest? pendingRequest = null;
                await foreach (var envelope in firstClient.ReadEventsAsync(0, firstTimeout.Token))
                {
                    if (envelope.RunId == runId
                        && envelope.MessageType == MessageTypes.MeetingHitlRequest)
                    {
                        pendingRequest = JsonSerializer.Deserialize(
                            envelope.Payload,
                            RuntimeJsonContext.Default.MeetingHitlRequest);
                        break;
                    }
                }

                hitlRequest = pendingRequest
                    ?? throw new AssertFailedException("Expected a meeting HITL request before restart.");
                Assert.IsNotNull(hitlRequest.ExpiresAt);
                firstShutdown.Cancel();
                await firstServerTask;
            }

            var secondInstanceId = Guid.NewGuid().ToString("N");
            await using (var secondServer = await RuntimeServer.StartAsync(
                             new RuntimeServerOptions(workspace)
                             {
                                 InstanceId = secondInstanceId,
                                 PipePrefix = $"madorin.meeting-hitl-timeout.second.{Guid.NewGuid():N}",
                                 HandshakeSecret = secret,
                                 MemoryUserHome = Path.Combine(workspace, "home"),
                                 ProviderResolver = selection =>
                                     new RecordingCompletingProvider(
                                         capturedRequests,
                                         selection.DefaultSelection.ProviderId)
                             },
                             TestContext.CancellationToken))
            {
                var secondServerTask = secondServer.RunAsync(secondShutdown.Token);
                await using var secondClient = new RuntimeClient(
                    new RuntimeClientOptions(
                        secondServer.PipeName,
                        Guid.NewGuid().ToString("N"),
                        ExpectedRuntimeInstanceId: secondInstanceId,
                        HandshakeSecret: secret));
                using var secondTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    TestContext.CancellationToken);
                secondTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                await secondClient.ConnectAsync(secondTimeout.Token);

                var requestEventCount = 0;
                var responseEventCount = 0;
                MeetingHitlResponse? timeoutResponse = null;
                RuntimeEventEnvelope? cancelled = null;
                await foreach (var envelope in secondClient.ReadEventsAsync(0, secondTimeout.Token))
                {
                    if (envelope.RunId != runId)
                    {
                        continue;
                    }

                    if (envelope.MessageType == MessageTypes.MeetingHitlRequest)
                    {
                        requestEventCount++;
                    }
                    else if (envelope.MessageType == MessageTypes.MeetingHitlResponse)
                    {
                        responseEventCount++;
                        timeoutResponse = JsonSerializer.Deserialize(
                            envelope.Payload,
                            RuntimeJsonContext.Default.MeetingHitlResponse);
                    }
                    else if (envelope.MessageType == MessageTypes.RunCancelled)
                    {
                        cancelled = envelope;
                        break;
                    }
                }

                Assert.IsNotNull(cancelled);
                Assert.AreEqual(1, requestEventCount);
                Assert.AreEqual(1, responseEventCount);
                Assert.IsNotNull(timeoutResponse);
                Assert.AreEqual(
                    hitlRequest.ApprovalRequestId,
                    timeoutResponse.ApprovalRequestId);
                Assert.AreEqual(MeetingHitlAction.Cancel, timeoutResponse.Action);
                Assert.AreEqual("HITL approval timed out", timeoutResponse.Reason);
                Assert.IsEmpty(capturedRequests);

                var resumed = await secondClient.ResumeSessionAsync(
                    hitlRequest.SessionId,
                    secondTimeout.Token);
                Assert.IsNotNull(resumed.MeetingState);
                Assert.AreEqual(
                    MeetingSessionStatus.Cancelled,
                    resumed.MeetingState.Status);
                Assert.IsNull(resumed.MeetingState.PendingApproval);
                Assert.AreEqual(runId, resumed.LatestRun?.RunId);
                Assert.AreEqual(RunStatus.Cancelled, resumed.LatestRun?.Status);

                secondShutdown.Cancel();
                await secondServerTask;
            }
        }
        finally
        {
            firstShutdown.Cancel();
            secondShutdown.Cancel();
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MeetingHitl_RunCancel_PersistsOneDecisionAndCancelsMeeting()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var capturedRequests = new ConcurrentBag<RuntimeProviderRequest>();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);

        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.meeting-hitl-cancel-e2e.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = selection =>
                        new RecordingCompletingProvider(
                            capturedRequests,
                            selection.DefaultSelection.ProviderId)
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);

            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await client.ConnectAsync(timeout.Token);

            var participants = new[]
            {
                new MeetingParticipant(
                    "participant-a",
                    new AgentRef("agent-a", "v1", "Agent A",
                        ProviderId: "hitl-provider-a",
                        ModelId: "hitl-model-a"),
                    "Agent A",
                    JoinOrder: 0),
                new MeetingParticipant(
                    "participant-b",
                    new AgentRef("agent-b", "v1", "Agent B",
                        ProviderId: "hitl-provider-b",
                        ModelId: "hitl-model-b"),
                    "Agent B",
                    JoinOrder: 1),
            };
            var meetingOptions = new MeetingModeOptions(
                participants,
                selectorPolicy: new MeetingSelectorPolicyOptions(MaxRounds: 1),
                policy: new MeetingPolicy(HitlEnabled: true, HitlTimeoutSeconds: 20));
            var runId = await client.StartNewSessionRunAsync(
                new NewSessionRunRequest(
                    "hitl-cancel-session",
                    "hitl-cancel-run",
                    RuntimeMode.Meeting,
                    new NextTurnSelection(
                        1,
                        RuntimeMode.Meeting,
                        new DefaultSelection("hitl-primary", "hitl-model"),
                        meetingOptions),
                    [new TextContentBlock("start meeting")]),
                timeout.Token);

            MeetingHitlRequest? hitlRequest = null;
            await foreach (var envelope in client.ReadEventsAsync(0, timeout.Token))
            {
                if (envelope.RunId == runId
                    && envelope.MessageType == MessageTypes.MeetingHitlRequest)
                {
                    hitlRequest = JsonSerializer.Deserialize(
                        envelope.Payload,
                        RuntimeJsonContext.Default.MeetingHitlRequest);
                    break;
                }
            }

            Assert.IsNotNull(hitlRequest);
            Assert.IsTrue(await client.CancelRunAsync(runId, timeout.Token));

            MeetingHitlResponse? persistedResponse = null;
            RuntimeEventEnvelope? cancelledEvent = null;
            var responseEventCount = 0;
            await foreach (var envelope in client.ReadEventsAsync(0, timeout.Token))
            {
                if (envelope.RunId != runId)
                {
                    continue;
                }

                if (envelope.MessageType == MessageTypes.MeetingHitlResponse)
                {
                    responseEventCount++;
                    persistedResponse = JsonSerializer.Deserialize(
                        envelope.Payload,
                        RuntimeJsonContext.Default.MeetingHitlResponse);
                }

                if (envelope.MessageType == MessageTypes.RunCancelled)
                {
                    cancelledEvent = envelope;
                    break;
                }
            }

            Assert.IsNotNull(cancelledEvent);
            Assert.AreEqual(1, responseEventCount);
            Assert.IsNotNull(persistedResponse);
            Assert.AreEqual(
                hitlRequest.ApprovalRequestId,
                persistedResponse.ApprovalRequestId);
            Assert.AreEqual(MeetingHitlAction.Cancel, persistedResponse.Action);
            Assert.IsEmpty(capturedRequests);

            var resume = await client.ResumeSessionAsync(
                hitlRequest.SessionId,
                timeout.Token);
            Assert.IsNotNull(resume.MeetingState);
            Assert.AreEqual(MeetingSessionStatus.Cancelled, resume.MeetingState.Status);
            Assert.IsNull(resume.MeetingState.PendingApproval);

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-meeting-hitl-e2e-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static MeetingParticipant[] CreateRecoveryParticipants() =>
    [
        new MeetingParticipant(
            "participant-a",
            new AgentRef(
                "agent-a",
                "v1",
                "Agent A",
                ProviderId: "recovery-provider-a",
                ModelId: "recovery-model-a"),
            "Agent A",
            JoinOrder: 0),
        new MeetingParticipant(
            "participant-b",
            new AgentRef(
                "agent-b",
                "v1",
                "Agent B",
                ProviderId: "recovery-provider-b",
                ModelId: "recovery-model-b"),
            "Agent B",
            JoinOrder: 1),
    ];

    private static JsonElement CreateCanonicalTextContent(string text) =>
        JsonSerializer.SerializeToElement(
            new ContentBlock[] { new TextContentBlock(text) },
            RuntimeJsonContext.Default.ContentBlockArray);

    private static AgentDefinition[] CreateRecoveryAgentDefinitions(
        MeetingParticipant[] participants,
        MeetingModeOptions meetingOptions)
    {
        var definitions = participants
            .Select(static participant => new AgentDefinition(
                participant.Agent,
                participant.ParticipantId))
            .ToList();
        if (meetingOptions.HostAgent is not null)
        {
            definitions.Add(new AgentDefinition(meetingOptions.HostAgent));
        }

        if (meetingOptions.SelectorPolicy?.SelectorAgent is not null)
        {
            definitions.Add(new AgentDefinition(meetingOptions.SelectorPolicy.SelectorAgent));
        }

        if (meetingOptions.Policy?.Summarizer is not null)
        {
            definitions.Add(new AgentDefinition(meetingOptions.Policy.Summarizer));
        }

        return [.. definitions];
    }

    private async Task<RuntimeProviderRequest[]> RunMeetingInvocationRecoveryScenarioAsync(
        MeetingParticipant[] participants,
        MeetingModeOptions meetingOptions,
        Func<RuntimeProviderRequest, bool> shouldBlock,
        Func<RuntimeProviderRequest, string> responseFactory,
        Func<string, string, RuntimeProviderRequest[], CancellationToken, Task>?
            afterFirstShutdown = null,
        int interruptedAttempts = 1,
        RunStatus expectedRunStatus = RunStatus.Completed,
        MeetingSessionStatus expectedMeetingStatus = MeetingSessionStatus.Completed,
        string? expectedTerminalText = null)
    {
        var workspace = CreateTemporaryDirectory();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var capturedRequests = new ConcurrentBag<RuntimeProviderRequest>();
        var firstProviderStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstProviderRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string runId;
        string sessionId;

        try
        {
            using (var firstShutdown = CancellationTokenSource.CreateLinkedTokenSource(
                       TestContext.CancellationToken))
            {
                var firstInstanceId = Guid.NewGuid().ToString("N");
                await using var firstServer = await RuntimeServer.StartAsync(
                    new RuntimeServerOptions(workspace)
                    {
                        InstanceId = firstInstanceId,
                        PipePrefix = $"madorin.meeting-role-recovery.first.{Guid.NewGuid():N}",
                        HandshakeSecret = secret,
                        MemoryUserHome = Path.Combine(workspace, "home"),
                        ProviderResolver = selection => new BlockingCompletingProvider(
                            capturedRequests,
                            firstProviderStarted,
                            firstProviderRelease,
                            selection.DefaultSelection.ProviderId,
                            shouldBlock,
                            responseFactory)
                    },
                    TestContext.CancellationToken);
                var firstServerTask = firstServer.RunAsync(firstShutdown.Token);
                await using var firstClient = new RuntimeClient(
                    new RuntimeClientOptions(
                        firstServer.PipeName,
                        Guid.NewGuid().ToString("N"),
                        ExpectedRuntimeInstanceId: firstInstanceId,
                        HandshakeSecret: secret));
                using var firstTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    TestContext.CancellationToken);
                firstTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await firstClient.ConnectAsync(firstTimeout.Token);

                runId = await firstClient.StartNewSessionRunAsync(
                    new NewSessionRunRequest(
                        "role-recovery-session",
                        "role-recovery-run",
                        RuntimeMode.Meeting,
                        new NextTurnSelection(
                            1,
                            RuntimeMode.Meeting,
                            new DefaultSelection("recovery-primary", "recovery-model"),
                            meetingOptions),
                        [new TextContentBlock("resume the interrupted meeting role")]),
                    firstTimeout.Token);
                await firstProviderStarted.Task.WaitAsync(firstTimeout.Token);
                sessionId = (await firstClient.QueryRunAsync(runId, firstTimeout.Token)).SessionId;

                firstShutdown.Cancel();
                await firstServerTask;
            }

            if (afterFirstShutdown is not null)
            {
                await afterFirstShutdown(
                    Path.Combine(workspace, ".madorin"),
                    sessionId,
                    [.. capturedRequests],
                    TestContext.CancellationToken);
            }

            var definitions = CreateRecoveryAgentDefinitions(participants, meetingOptions);
            for (var attempt = 1; attempt < interruptedAttempts; attempt++)
            {
                var intermediateProviderStarted = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var intermediateProviderRelease = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                using var intermediateShutdown = CancellationTokenSource.CreateLinkedTokenSource(
                    TestContext.CancellationToken);
                var intermediateInstanceId = Guid.NewGuid().ToString("N");
                await using var intermediateServer = await RuntimeServer.StartAsync(
                    new RuntimeServerOptions(workspace)
                    {
                        InstanceId = intermediateInstanceId,
                        PipePrefix = $"madorin.meeting-role-recovery.intermediate.{Guid.NewGuid():N}",
                        HandshakeSecret = secret,
                        MemoryUserHome = Path.Combine(workspace, "home"),
                        ProviderResolver = selection => new BlockingCompletingProvider(
                            capturedRequests,
                            intermediateProviderStarted,
                            intermediateProviderRelease,
                            selection.DefaultSelection.ProviderId,
                            shouldBlock,
                            responseFactory)
                    },
                    TestContext.CancellationToken);
                var intermediateServerTask = intermediateServer.RunAsync(intermediateShutdown.Token);
                await using var intermediateClient = new RuntimeClient(
                    new RuntimeClientOptions(
                        intermediateServer.PipeName,
                        Guid.NewGuid().ToString("N"),
                        ExpectedRuntimeInstanceId: intermediateInstanceId,
                        HandshakeSecret: secret));
                using var intermediateTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    TestContext.CancellationToken);
                intermediateTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await intermediateClient.ConnectAsync(intermediateTimeout.Token);
                var intermediateResume = await intermediateClient.ResumeSessionAsync(
                    sessionId,
                    intermediateTimeout.Token);
                Assert.AreEqual(RunStatus.Interrupted, intermediateResume.LatestRun?.Status);
                var intermediateRehydrate = await intermediateClient.RehydrateSessionAsync(
                    new SessionRehydrateParameters(sessionId, definitions),
                    intermediateTimeout.Token);
                Assert.AreEqual("Ready", intermediateRehydrate.Status);
                await intermediateProviderStarted.Task.WaitAsync(intermediateTimeout.Token);

                intermediateShutdown.Cancel();
                await intermediateServerTask;
            }

            using var secondShutdown = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            var secondInstanceId = Guid.NewGuid().ToString("N");
            await using var secondServer = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = secondInstanceId,
                    PipePrefix = $"madorin.meeting-role-recovery.second.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = selection => new RecordingCompletingProvider(
                        capturedRequests,
                        selection.DefaultSelection.ProviderId,
                        responseFactory)
                },
                TestContext.CancellationToken);
            var secondServerTask = secondServer.RunAsync(secondShutdown.Token);
            await using var secondClient = new RuntimeClient(
                new RuntimeClientOptions(
                    secondServer.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: secondInstanceId,
                    HandshakeSecret: secret));
            using var secondTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            secondTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await secondClient.ConnectAsync(secondTimeout.Token);

            var beforeRehydrate = await secondClient.ResumeSessionAsync(
                sessionId,
                secondTimeout.Token);
            Assert.AreEqual(runId, beforeRehydrate.LatestRun?.RunId);
            Assert.AreEqual(RunStatus.Interrupted, beforeRehydrate.LatestRun?.Status);
            Assert.AreEqual(
                MeetingInvocationStatus.Interrupted,
                beforeRehydrate.MeetingState?.NextScheduledInvocation?.Status);

            var rehydrate = await secondClient.RehydrateSessionAsync(
                new SessionRehydrateParameters(sessionId, definitions),
                secondTimeout.Token);
            Assert.AreEqual("Ready", rehydrate.Status);
            Assert.IsEmpty(rehydrate.Mismatched);

            SessionResumeResult? finalResume = null;
            if (expectedRunStatus == RunStatus.Completed)
            {
                RuntimeEventEnvelope? terminal = null;
                await foreach (var envelope in secondClient.ReadEventsAsync(0, secondTimeout.Token))
                {
                    if (envelope.RunId == runId
                        && envelope.MessageType is MessageTypes.RunCompleted
                            or MessageTypes.RunFailed
                            or MessageTypes.RunCancelled)
                    {
                        terminal = envelope;
                        break;
                    }
                }

                Assert.IsNotNull(terminal);
                Assert.AreEqual(
                    MessageTypes.RunCompleted,
                    terminal.MessageType,
                    terminal.Payload.GetRawText());
                finalResume = await secondClient.ResumeSessionAsync(
                    sessionId,
                    secondTimeout.Token);
            }
            else
            {
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    finalResume = await secondClient.ResumeSessionAsync(
                        sessionId,
                        secondTimeout.Token);
                    if (finalResume.LatestRun?.Status == expectedRunStatus)
                    {
                        break;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(20), secondTimeout.Token);
                }
            }

            Assert.IsNotNull(finalResume);
            Assert.AreEqual(expectedRunStatus, finalResume.LatestRun?.Status);
            Assert.AreEqual(expectedMeetingStatus, finalResume.MeetingState?.Status);
            if (expectedTerminalText is not null)
            {
                Assert.AreEqual(expectedTerminalText, finalResume.LatestRun?.TerminalText);
            }

            if (expectedMeetingStatus == MeetingSessionStatus.Completed)
            {
                Assert.IsNull(finalResume.MeetingState?.NextScheduledInvocation);
            }
            else
            {
                Assert.AreEqual(
                    MeetingInvocationStatus.Interrupted,
                    finalResume.MeetingState?.NextScheduledInvocation?.Status);
            }

            secondShutdown.Cancel();
            await secondServerTask;
            return [.. capturedRequests];
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    private sealed class RecordingCompletingProvider(
        ConcurrentBag<RuntimeProviderRequest> capturedRequests,
        string providerId = "recording",
        Func<RuntimeProviderRequest, string>? responseFactory = null) : IRuntimeProviderAdapter
    {
        public string ProviderId => providerId;

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("recording-model", "Recording Model", ContextWindow: 8192)];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            capturedRequests.Add(request);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new TextDeltaProviderEvent(
                request.InvocationId,
                responseFactory?.Invoke(request) ?? "meeting response");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class BlockingCompletingProvider(
        ConcurrentBag<RuntimeProviderRequest> capturedRequests,
        TaskCompletionSource started,
        TaskCompletionSource release,
        string providerId,
        Func<RuntimeProviderRequest, bool>? shouldBlock = null,
        Func<RuntimeProviderRequest, string>? responseFactory = null) : IRuntimeProviderAdapter
    {
        public string ProviderId => providerId;

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("blocking-model", "Blocking Model", ContextWindow: 8192)];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            capturedRequests.Add(request);
            if (shouldBlock?.Invoke(request) ?? true)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
            }

            yield return new TextDeltaProviderEvent(
                request.InvocationId,
                responseFactory?.Invoke(request) ?? "meeting response");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
