using Madorin.AI.Runtime.Core;
using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Core.Tests;

[TestClass]
public sealed class RunStateMachineTests
{
    [TestMethod]
    [DataRow(RunStatus.Accepted, RunStatus.Preparing)]
    [DataRow(RunStatus.Preparing, RunStatus.Running)]
    [DataRow(RunStatus.Running, RunStatus.WaitingForTool)]
    [DataRow(RunStatus.WaitingForTool, RunStatus.Running)]
    [DataRow(RunStatus.Running, RunStatus.Persisting)]
    [DataRow(RunStatus.Persisting, RunStatus.Completed)]
    public void CanTransition_WithDocumentedPath_ReturnsTrue(
        RunStatus current,
        RunStatus next)
    {
        Assert.IsTrue(RunStateMachine.CanTransition(current, next));
    }

    [TestMethod]
    [DataRow(RunStatus.Completed)]
    [DataRow(RunStatus.Failed)]
    [DataRow(RunStatus.Cancelled)]
    public void CanTransition_FromTerminalState_ReturnsFalse(RunStatus current)
    {
        Assert.IsFalse(RunStateMachine.CanTransition(current, RunStatus.Running));
    }

    [TestMethod]
    public void CanTransition_FromActiveStateToCancelled_ReturnsTrue()
    {
        Assert.IsTrue(
            RunStateMachine.CanTransition(RunStatus.WaitingForApproval, RunStatus.Cancelled));
    }
}
