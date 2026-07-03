namespace Netor.Cortana.AI.Tests.Chat;

[TestClass]
public sealed class ChatTurnCancellationRegistryTests
{
    [TestMethod]
    public void Register_WhenTurnRegistered_HasActiveTurnReturnsTrue()
    {
        var registry = new ChatTurnCancellationRegistry();
        using var cts = new CancellationTokenSource();
        var turnContext = new ChatTurnContext("turn-1", null, null, cts);

        registry.Register(turnContext);

        Assert.IsTrue(registry.HasActiveTurn());
        Assert.AreSame(turnContext, registry.GetCurrentTurn());
    }

    [TestMethod]
    public void CancelCurrentTurn_WhenTurnExists_CancelsItsToken()
    {
        var registry = new ChatTurnCancellationRegistry();
        using var cts = new CancellationTokenSource();
        var turnContext = new ChatTurnContext("turn-1", null, null, cts);
        registry.Register(turnContext);

        var cancelled = registry.CancelCurrentTurn();

        Assert.AreSame(turnContext, cancelled);
        Assert.IsTrue(cts.IsCancellationRequested);
    }

    [TestMethod]
    public void ClearCurrentTurn_WhenTurnIdMatches_ClearsRegisteredTurn()
    {
        var registry = new ChatTurnCancellationRegistry();
        using var cts = new CancellationTokenSource();
        var turnContext = new ChatTurnContext("turn-1", null, null, cts);
        registry.Register(turnContext);

        var cleared = registry.ClearCurrentTurn("turn-1");

        Assert.AreSame(turnContext, cleared);
        Assert.IsFalse(registry.HasActiveTurn());
        Assert.IsNull(registry.GetCurrentTurn());
    }

    [TestMethod]
    public void ChatTurnContext_CompletionAndCancellationFlags_AreIdempotent()
    {
        using var cts = new CancellationTokenSource();
        var turnContext = new ChatTurnContext("turn-1", null, null, cts);

        Assert.IsTrue(turnContext.TryMarkCancellationNotified());
        Assert.IsFalse(turnContext.TryMarkCancellationNotified());
        Assert.IsTrue(turnContext.TryMarkCompletionPublished());
        Assert.IsFalse(turnContext.TryMarkCompletionPublished());
        Assert.IsTrue(turnContext.IsCompletionPublished);
    }
}
