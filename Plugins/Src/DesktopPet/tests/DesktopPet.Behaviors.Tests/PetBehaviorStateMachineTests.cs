using DesktopPet.Behaviors;

namespace DesktopPet.Behaviors.Tests;

[TestClass]
public sealed class PetBehaviorStateMachineTests
{
    [TestMethod]
    public void Current_StartsIdleAndVisible()
    {
        var stateMachine = new PetBehaviorStateMachine();

        var snapshot = stateMachine.Current;

        Assert.AreEqual(PetState.Idle, snapshot.State);
        Assert.AreEqual(string.Empty, snapshot.Subtitle);
        Assert.IsTrue(snapshot.IsVisible);
        Assert.IsFalse(snapshot.IsSpeaking);
        Assert.IsFalse(snapshot.IsThinking);
    }

    [TestMethod]
    public void Apply_Think_SetsThinkingAndSubtitle()
    {
        var stateMachine = new PetBehaviorStateMachine();
        var occurredAt = new DateTimeOffset(2026, 5, 25, 9, 30, 0, TimeSpan.Zero);

        var snapshot = stateMachine.Apply(new PetEvent(PetEventKind.Think, "思考中", occurredAt));

        Assert.AreEqual(PetState.Thinking, snapshot.State);
        Assert.AreEqual("思考中", snapshot.Subtitle);
        Assert.IsTrue(snapshot.IsThinking);
        Assert.AreEqual(occurredAt, snapshot.UpdatedAt);
    }

    [TestMethod]
    public void Apply_TextDelta_AppendsTextAndSetsSpeaking()
    {
        var stateMachine = new PetBehaviorStateMachine();

        stateMachine.Apply(new PetEvent(PetEventKind.TextDelta, "你好"));
        var snapshot = stateMachine.Apply(new PetEvent(PetEventKind.TextDelta, "，桌面。"));

        Assert.AreEqual(PetState.Speaking, snapshot.State);
        Assert.AreEqual("你好，桌面。", snapshot.Subtitle);
        Assert.IsTrue(snapshot.IsSpeaking);
    }

    [TestMethod]
    public void Apply_Hide_ClearsSubtitleAndIgnoresSpeechUntilShown()
    {
        var stateMachine = new PetBehaviorStateMachine();

        stateMachine.Apply(new PetEvent(PetEventKind.Speak, "先说一句"));
        var hidden = stateMachine.Apply(new PetEvent(PetEventKind.Hide));
        var stillHidden = stateMachine.Apply(new PetEvent(PetEventKind.TextDelta, "不会显示"));
        var shown = stateMachine.Apply(new PetEvent(PetEventKind.Show));

        Assert.AreEqual(PetState.Hidden, hidden.State);
        Assert.AreEqual(string.Empty, hidden.Subtitle);
        Assert.IsFalse(hidden.IsVisible);
        Assert.AreEqual(PetState.Hidden, stillHidden.State);
        Assert.AreEqual(string.Empty, stillHidden.Subtitle);
        Assert.AreEqual(PetState.Idle, shown.State);
        Assert.IsTrue(shown.IsVisible);
    }

    [TestMethod]
    public void Apply_ClearText_ClearsSubtitleAndReturnsToIdle()
    {
        var stateMachine = new PetBehaviorStateMachine();

        stateMachine.Apply(new PetEvent(PetEventKind.Speak, "正在说话"));
        var snapshot = stateMachine.Apply(new PetEvent(PetEventKind.ClearText));

        Assert.AreEqual(PetState.Idle, snapshot.State);
        Assert.AreEqual(string.Empty, snapshot.Subtitle);
        Assert.IsFalse(snapshot.IsSpeaking);
    }
}
