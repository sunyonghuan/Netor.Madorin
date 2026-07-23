using Madorin.AI.Runtime.Core;
using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Core.Tests;

[TestClass]
public sealed class RunStateMachineTests
{
    private static readonly HashSet<(RunStatus Current, RunStatus Next)> AllowedTransitions =
    [
        (RunStatus.Accepted, RunStatus.Preparing),
        (RunStatus.Accepted, RunStatus.Cancelled),
        (RunStatus.Preparing, RunStatus.Running),
        (RunStatus.Preparing, RunStatus.Failed),
        (RunStatus.Preparing, RunStatus.Cancelled),
        (RunStatus.Running, RunStatus.WaitingForTool),
        (RunStatus.Running, RunStatus.WaitingForApproval),
        (RunStatus.Running, RunStatus.WaitingForCredentials),
        (RunStatus.Running, RunStatus.Persisting),
        (RunStatus.Running, RunStatus.Failed),
        (RunStatus.Running, RunStatus.Cancelled),
        (RunStatus.WaitingForTool, RunStatus.Running),
        (RunStatus.WaitingForTool, RunStatus.WaitingForApproval),
        (RunStatus.WaitingForTool, RunStatus.Failed),
        (RunStatus.WaitingForTool, RunStatus.Cancelled),
        (RunStatus.WaitingForApproval, RunStatus.Running),
        (RunStatus.WaitingForApproval, RunStatus.WaitingForTool),
        (RunStatus.WaitingForApproval, RunStatus.Failed),
        (RunStatus.WaitingForApproval, RunStatus.Cancelled),
        (RunStatus.WaitingForCredentials, RunStatus.Running),
        (RunStatus.WaitingForCredentials, RunStatus.Failed),
        (RunStatus.WaitingForCredentials, RunStatus.Cancelled),
        (RunStatus.Persisting, RunStatus.Completed),
        (RunStatus.Persisting, RunStatus.Failed),
        (RunStatus.Persisting, RunStatus.Cancelled)
    ];

    public static IEnumerable<object[]> GetAllTransitionCases()
    {
        foreach (var current in Enum.GetValues<RunStatus>())
        {
            foreach (var next in Enum.GetValues<RunStatus>())
            {
                yield return [current, next, AllowedTransitions.Contains((current, next))];
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(GetAllTransitionCases))]
    public void CanTransition_ForEveryStatusPair_MatchesExplicitTransitionTable(
        RunStatus current,
        RunStatus next,
        bool expected)
    {
        Assert.AreEqual(expected, RunStateMachine.CanTransition(current, next));
    }

    [TestMethod]
    [DataRow(RunStatus.Accepted, RunStatus.Preparing)]
    [DataRow(RunStatus.Preparing, RunStatus.Running)]
    [DataRow(RunStatus.Running, RunStatus.WaitingForTool)]
    [DataRow(RunStatus.WaitingForTool, RunStatus.Running)]
    [DataRow(RunStatus.WaitingForTool, RunStatus.WaitingForApproval)]
    [DataRow(RunStatus.WaitingForApproval, RunStatus.WaitingForTool)]
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
    [DataRow(RunStatus.Interrupted)]
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
