using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Netor.Cortana.AI.Orchestration;
using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.AI.Tests.Chat;

[TestClass]
public sealed class ChatGenerationTurnCoordinatorTests
{
    private ServiceProvider _services = null!;

    [TestInitialize]
    public void Setup()
    {
        _services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .BuildServiceProvider();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _services.Dispose();
    }

    [TestMethod]
    public async Task Start_WhenInvoked_RegistersTurnAndPublishesConversationStart()
    {
        var turnRegistry = new ChatTurnCancellationRegistry();
        var startedSignals = new List<VoiceSignalArgs>();
        var startedEvents = new List<ConversationTurnStartedArgs>();
        var userEvents = new List<ConversationUserMessageArgs>();
        SubscribeVoiceSignals(startedSignals, Events.OnAiStarted);
        Subscribe(startedEvents, Events.OnConversationTurnStarted.Eventid);
        Subscribe(userEvents, Events.OnConversationUserMessage.Eventid);

        var coordinator = CreateCoordinator(turnRegistry);
        var provider = CreateProvider();
        var agent = CreateAgent();
        var model = CreateModel(provider.Id);
        var session = new TestAgentSession();
        session.StateBag.SetValue("sessionid", "session-1");
        var savedMessages = new List<Microsoft.Extensions.AI.ChatMessage>();

        var state = coordinator.Start(
            "draw a cat",
            "image",
            "图片生成中",
            turnAgent: null!,
            turnSession: session,
            turnProvider: provider,
            turnAgentEntity: agent,
            turnModel: model,
            saveUserMessage: (message, _, _, _) => savedMessages.Add(message),
            createConversationEventMetadata: CreateMetadata,
            cancellationToken: CancellationToken.None);

        await WaitForCountAsync(startedSignals, 1);
        await WaitForCountAsync(startedEvents, 1);
        await WaitForCountAsync(userEvents, 1);

        Assert.AreEqual("image", state.GenerationKind);
        Assert.AreEqual("session-1", state.CurrentSessionId);
        Assert.AreEqual(state.TurnId, session.StateBag.GetValue<string>("turnid"));
        Assert.AreEqual(state.TraceId, session.StateBag.GetValue<string>("traceid"));
        Assert.AreEqual(state.UserMessageId, session.StateBag.GetValue<string>("usermessageid"));
        Assert.AreEqual(state.AssistantMessageId, session.StateBag.GetValue<string>("assistantmessageid"));
        Assert.AreSame(state.TurnContext, turnRegistry.GetCurrentTurn());
        Assert.AreEqual("draw a cat", savedMessages.Single().Text);
        Assert.AreEqual(state.TurnId, startedEvents[0].TurnId);
        Assert.AreEqual("draw a cat", userEvents[0].Content);
    }

    [TestMethod]
    public async Task PublishFailedAsync_WhenInvoked_EmitsFailedRealtimeEvent()
    {
        var turnRegistry = new ChatTurnCancellationRegistry();
        var realtimeOutput = new FakeRealtimeProcessOutput();
        var coordinator = CreateCoordinator(turnRegistry, realtimeOutput);
        var state = CreateState();

        await coordinator.PublishFailedAsync(state, "boom");

        Assert.HasCount(1, realtimeOutput.Events);
        Assert.AreEqual(state.TurnId, realtimeOutput.Events[0].TurnId);
        Assert.AreEqual(state.ProcessId, realtimeOutput.Events[0].ProcessId);
        Assert.AreEqual("failed", realtimeOutput.Events[0].Status);
        Assert.AreEqual("boom", realtimeOutput.Events[0].Content);
    }

    [TestMethod]
    public async Task CompleteTurn_WhenInvoked_ClearsTurnAndPublishesCompletedSignal()
    {
        var turnRegistry = new ChatTurnCancellationRegistry();
        var completedSignals = new List<VoiceSignalArgs>();
        SubscribeVoiceSignals(completedSignals, Events.OnAiCompleted);
        var coordinator = CreateCoordinator(turnRegistry);
        var state = CreateState();
        turnRegistry.Register(state.TurnContext);

        coordinator.CompleteTurn(state);

        await WaitForCountAsync(completedSignals, 1);
        Assert.IsNull(turnRegistry.GetCurrentTurn());
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = state.StreamCts.Token.WaitHandle);
    }

    private ChatGenerationTurnCoordinator CreateCoordinator(
        ChatTurnCancellationRegistry turnRegistry,
        IRealtimeProcessOutput? realtimeOutput = null)
    {
        return new ChatGenerationTurnCoordinator(
            turnRegistry,
            new ChatConversationEventPublisher(_services.GetRequiredService<IPublisher>()),
            new ChatRealtimeEventPublisher(realtimeOutput),
            _services.GetRequiredService<IPublisher>());
    }

    private static ConversationEventMetadata CreateMetadata(
        string sessionId,
        string turnId,
        string traceId,
        string userMessageId,
        string assistantMessageId,
        AiProviderEntity provider,
        AgentEntity agent,
        AiModelEntity model,
        AgentOrchestrationResult? orchestrationResult)
    {
        return new ConversationEventMetadata(
            sessionId,
            turnId,
            traceId,
            provider.Id,
            provider.Name,
            agent.Id,
            agent.Name,
            model.Id,
            model.Name,
            userMessageId,
            assistantMessageId,
            orchestrationResult?.Mode ?? AgentOrchestrationMode.None,
            orchestrationResult?.UsedAgentIds,
            orchestrationResult?.Warnings);
    }

    private static AiProviderEntity CreateProvider()
    {
        return new AiProviderEntity
        {
            Id = "provider-1",
            Name = "Provider 1",
        };
    }

    private static AgentEntity CreateAgent()
    {
        return new AgentEntity
        {
            Id = "agent-1",
            Name = "Agent 1",
            Instructions = "test",
            IsEnabled = true,
        };
    }

    private static AiModelEntity CreateModel(string providerId)
    {
        return new AiModelEntity
        {
            Id = "model-1",
            Name = "Model 1",
            ProviderId = providerId,
            OutputCapabilities = OutputCapabilities.Text,
        };
    }

    private static ChatGenerationTurnState CreateState()
    {
        var cts = new CancellationTokenSource();
        return new ChatGenerationTurnState(
            TurnId: "turn-1",
            GenerationKind: "image",
            QueuedMessage: "queued",
            TraceId: "trace-1",
            UserMessageId: "user-1",
            AssistantMessageId: "assistant-1",
            CurrentSessionId: "session-1",
            Metadata: new ConversationEventMetadata(
                "session-1",
                "turn-1",
                "trace-1",
                "provider-1",
                "Provider 1",
                "agent-1",
                "Agent 1",
                "model-1",
                "Model 1",
                "user-1",
                "assistant-1",
                AgentOrchestrationMode.None,
                [],
                []),
            TurnContext: new ChatTurnContext("turn-1", null, null, cts),
            StreamCts: cts);
    }

    private void Subscribe<TArgs>(List<TArgs> sink, string eventId)
        where TArgs : IEventArgs
    {
        _ = _services.GetRequiredService<ISubscriber>().Subscribe<TArgs>(
            eventId,
            (_, args) =>
            {
                sink.Add(args);
                return Task.FromResult(false);
            });
    }

    private void SubscribeVoiceSignals(List<VoiceSignalArgs> sink, VoiceSignalEvent evt)
    {
        _ = _services.GetRequiredService<ISubscriber>().Subscribe<VoiceSignalArgs>(
            evt.Eventid,
            (_, args) =>
            {
                sink.Add(args);
                return Task.FromResult(false);
            });
    }

    private static async Task WaitForCountAsync<T>(ICollection<T> collection, int expectedCount)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (collection.Count < expectedCount)
        {
            await Task.Delay(20, cts.Token);
        }
    }

    private sealed class FakeRealtimeProcessOutput : IRealtimeProcessOutput
    {
        public List<RealtimeProcessEvent> Events { get; } = [];

        public Task OnProcessEventAsync(RealtimeProcessEvent evt, CancellationToken ct = default)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }
    }

    private sealed class TestAgentSession : AgentSession
    {
    }
}
