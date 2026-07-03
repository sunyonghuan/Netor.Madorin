using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Orchestration;
using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.AI.Tests.Chat;

[TestClass]
public sealed class ChatConversationEventPublisherTests
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
    public async Task PublishTurnStarted_WhenMetadataContainsOrchestrationInfo_PublishesOriginalEventShape()
    {
        var events = new List<ConversationTurnStartedArgs>();
        _ = _services.GetRequiredService<ISubscriber>().Subscribe<ConversationTurnStartedArgs>(
            Events.OnConversationTurnStarted,
            (_, args) =>
            {
                events.Add(args);
                return Task.FromResult(false);
            });

        var logger = new ListLogger<ChatConversationEventPublisher>();
        var diagnostics = new ChatOrchestrationDiagnosticsService();
        var publisher = new ChatConversationEventPublisher(
            _services.GetRequiredService<IPublisher>(),
            diagnostics,
            logger);

        publisher.PublishTurnStarted(
            CreateMetadata(),
            attachmentCount: 2,
            mentionedAgentIds: ["agent-a", "agent-b"]);

        await WaitForCountAsync(events, 1);
        Assert.AreEqual("turn-1", events[0].TurnId);
        Assert.AreEqual(2, events[0].AttachmentCount);
        CollectionAssert.AreEqual(new[] { "agent-a", "agent-b" }, events[0].MentionedAgentIds.ToArray());
        Assert.IsTrue(logger.Messages.Any(static x => x.Contains("ConversationTurnStarted", StringComparison.Ordinal)));
        var snapshot = diagnostics.GetLatestSnapshot();
        Assert.IsNotNull(snapshot);
        Assert.AreEqual("turn-1", snapshot.TurnId);
        Assert.AreEqual(AgentOrchestrationMode.ToolDelegation, snapshot.Mode);
        Assert.IsFalse(snapshot.IsCompleted);
    }

    [TestMethod]
    public async Task PublishTurnCompleted_WhenMetadataContainsOrchestrationInfo_PublishesOriginalEventShape()
    {
        var events = new List<ConversationTurnCompletedArgs>();
        _ = _services.GetRequiredService<ISubscriber>().Subscribe<ConversationTurnCompletedArgs>(
            Events.OnConversationTurnCompleted,
            (_, args) =>
            {
                events.Add(args);
                return Task.FromResult(false);
            });

        var logger = new ListLogger<ChatConversationEventPublisher>();
        var diagnostics = new ChatOrchestrationDiagnosticsService();
        var publisher = new ChatConversationEventPublisher(
            _services.GetRequiredService<IPublisher>(),
            diagnostics,
            logger);

        publisher.PublishTurnCompleted(
            CreateMetadata(),
            ConversationTurnStatus.Succeeded,
            "hi",
            "hello",
            null,
            assistantDeltaCount: 3,
            attachmentCount: 1);

        await WaitForCountAsync(events, 1);
        Assert.AreEqual("turn-1", events[0].TurnId);
        Assert.AreEqual(ConversationTurnStatus.Succeeded, events[0].Status);
        Assert.AreEqual("hello", events[0].AssistantResponse);
        Assert.IsTrue(logger.Messages.Any(static x => x.Contains("ConversationTurnCompleted", StringComparison.Ordinal)));
        var snapshot = diagnostics.GetLatestSnapshot();
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(ConversationTurnStatus.Succeeded, snapshot.Status);
        Assert.IsTrue(snapshot.IsCompleted);
        CollectionAssert.AreEqual(new[] { "agent-a", "agent-b" }, snapshot.AgentIds.ToArray());
    }

    [TestMethod]
    public void GetSnapshot_WhenMultipleSessionsRecorded_ReturnsSessionScopedSnapshot()
    {
        var diagnostics = new ChatOrchestrationDiagnosticsService();
        var publisher = new ChatConversationEventPublisher(
            _services.GetRequiredService<IPublisher>(),
            diagnostics);

        publisher.PublishTurnStarted(
            CreateMetadata(sessionId: "session-a", turnId: "turn-a"),
            attachmentCount: 0,
            mentionedAgentIds: []);
        publisher.PublishTurnCompleted(
            CreateMetadata(sessionId: "session-b", turnId: "turn-b"),
            ConversationTurnStatus.Cancelled,
            "user",
            "assistant",
            null,
            assistantDeltaCount: 1,
            attachmentCount: 0);

        var sessionASnapshot = diagnostics.GetSnapshot("session-a");
        var sessionBSnapshot = diagnostics.GetSnapshot("session-b");

        Assert.IsNotNull(sessionASnapshot);
        Assert.IsNotNull(sessionBSnapshot);
        Assert.AreEqual("turn-a", sessionASnapshot.TurnId);
        Assert.IsFalse(sessionASnapshot.IsCompleted);
        Assert.AreEqual("turn-b", sessionBSnapshot.TurnId);
        Assert.AreEqual(ConversationTurnStatus.Cancelled, sessionBSnapshot.Status);
        Assert.IsTrue(sessionBSnapshot.IsCompleted);
    }

    [TestMethod]
    public void GetRecentSnapshots_WhenTurnUpdated_KeepsLatestStateAndNewestFirst()
    {
        var diagnostics = new ChatOrchestrationDiagnosticsService();
        var publisher = new ChatConversationEventPublisher(
            _services.GetRequiredService<IPublisher>(),
            diagnostics);

        publisher.PublishTurnStarted(
            CreateMetadata(sessionId: "session-1", turnId: "turn-1"),
            attachmentCount: 0,
            mentionedAgentIds: []);
        publisher.PublishTurnCompleted(
            CreateMetadata(sessionId: "session-1", turnId: "turn-1"),
            ConversationTurnStatus.Succeeded,
            "user-1",
            "assistant-1",
            null,
            assistantDeltaCount: 1,
            attachmentCount: 0);
        publisher.PublishTurnStarted(
            CreateMetadata(sessionId: "session-1", turnId: "turn-2"),
            attachmentCount: 0,
            mentionedAgentIds: []);

        var history = diagnostics.GetRecentSnapshots("session-1");

        Assert.HasCount(2, history);
        Assert.AreEqual("turn-2", history[0].TurnId);
        Assert.IsFalse(history[0].IsCompleted);
        Assert.AreEqual("turn-1", history[1].TurnId);
        Assert.IsTrue(history[1].IsCompleted);
        Assert.AreEqual(ConversationTurnStatus.Succeeded, history[1].Status);
    }

    [TestMethod]
    public void GetRecentSnapshots_WhenMoreThanLimit_OnlyKeepsLatestSix()
    {
        var diagnostics = new ChatOrchestrationDiagnosticsService();
        var publisher = new ChatConversationEventPublisher(
            _services.GetRequiredService<IPublisher>(),
            diagnostics);

        for (var i = 1; i <= 8; i++)
        {
            publisher.PublishTurnStarted(
                CreateMetadata(sessionId: "session-limit", turnId: $"turn-{i}"),
                attachmentCount: 0,
                mentionedAgentIds: []);
        }

        var history = diagnostics.GetRecentSnapshots("session-limit");

        Assert.HasCount(6, history);
        Assert.AreEqual("turn-8", history[0].TurnId);
        Assert.AreEqual("turn-3", history[^1].TurnId);
    }

    private static ConversationEventMetadata CreateMetadata(
        string sessionId = "session-1",
        string turnId = "turn-1")
    {
        return new ConversationEventMetadata(
            sessionId,
            turnId,
            "trace-1",
            "provider-1",
            "Provider 1",
            "agent-main",
            "Agent Main",
            "model-1",
            "Model 1",
            "user-1",
            "assistant-1",
            AgentOrchestrationMode.ToolDelegation,
            ["agent-a", "agent-b"],
            ["fallback"]);
    }

    private static async Task WaitForCountAsync<T>(ICollection<T> collection, int expectedCount)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (collection.Count < expectedCount)
        {
            await Task.Delay(20, cts.Token);
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
