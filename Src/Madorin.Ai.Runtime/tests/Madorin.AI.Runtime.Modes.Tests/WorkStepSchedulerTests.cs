using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Modes.Work;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
public sealed class WorkStepSchedulerTests
{
    private static readonly string[] ExpectedReadyOrder = ["step-a", "step-b"];
    private static readonly string[] ExpectedBranchReadyOrder = ["child-a", "child-b"];

    [TestMethod]
    public void SelectReadySteps_WithSatisfiedDependencies_ReturnsPendingReadyStepsInStableOrder()
    {
        var policy = WorkflowPolicy.Default with { MaxConcurrentSteps = 3 };
        WorkStepSnapshot[] steps =
        [
            Step("root", WorkStepLifecycleStatus.Completed),
            Step("step-b", WorkStepLifecycleStatus.Pending, depth: 1, dependsOn: ["root"]),
            Step("step-a", WorkStepLifecycleStatus.Pending),
            Step("blocked", WorkStepLifecycleStatus.Pending, dependsOn: ["missing"]),
            Step("running", WorkStepLifecycleStatus.Running)
        ];

        var ready = WorkStepScheduler.SelectReadySteps(steps, policy);

        CollectionAssert.AreEqual(
            ExpectedReadyOrder,
            ready.Select(static step => step.StepId).ToArray());
    }

    [TestMethod]
    public void SelectReadySteps_WithCurrentRunningCount_RespectsRemainingCapacity()
    {
        var policy = WorkflowPolicy.Default with { MaxConcurrentSteps = 2 };
        WorkStepSnapshot[] steps =
        [
            Step("step-a", WorkStepLifecycleStatus.Pending),
            Step("step-b", WorkStepLifecycleStatus.Pending),
            Step("step-c", WorkStepLifecycleStatus.Pending)
        ];

        var ready = WorkStepScheduler.SelectReadySteps(
            steps,
            policy,
            currentlyRunningCount: 1);

        var onlyStep = Assert.ContainsSingle(ready);
        Assert.AreEqual("step-a", onlyStep.StepId);
    }

    [TestMethod]
    public void SelectReadySteps_WithBranchDependencies_ReturnsAllReadyChildren()
    {
        var policy = WorkflowPolicy.Default with { MaxConcurrentSteps = 2 };
        WorkStepSnapshot[] steps =
        [
            Step("root", WorkStepLifecycleStatus.Completed),
            Step("child-a", WorkStepLifecycleStatus.Pending, depth: 1, dependsOn: ["root"]),
            Step("child-b", WorkStepLifecycleStatus.Pending, depth: 1, dependsOn: ["root"])
        ];

        var ready = WorkStepScheduler.SelectReadySteps(steps, policy);

        CollectionAssert.AreEqual(
            ExpectedBranchReadyOrder,
            ready.Select(static step => step.StepId).ToArray());
    }

    [TestMethod]
    public void SelectReadySteps_WhenCapacityIsExhausted_ReturnsNoSteps()
    {
        var policy = WorkflowPolicy.Default with { MaxConcurrentSteps = 1 };
        WorkStepSnapshot[] steps =
        [
            Step("step-a", WorkStepLifecycleStatus.Pending)
        ];

        var ready = WorkStepScheduler.SelectReadySteps(
            steps,
            policy,
            currentlyRunningCount: 1);

        Assert.HasCount(0, ready);
    }

    [TestMethod]
    public void SelectReadySteps_WithFailedParent_DoesNotScheduleChild()
    {
        var policy = WorkflowPolicy.Default with { MaxConcurrentSteps = 2 };
        WorkStepSnapshot[] steps =
        [
            Step("parent", WorkStepLifecycleStatus.Failed),
            Step("child", WorkStepLifecycleStatus.Pending, parentStepId: "parent"),
            Step("independent", WorkStepLifecycleStatus.Pending)
        ];

        var ready = WorkStepScheduler.SelectReadySteps(steps, policy);

        var onlyStep = Assert.ContainsSingle(ready);
        Assert.AreEqual("independent", onlyStep.StepId);
    }

    [TestMethod]
    public void SelectReadySteps_WithSkippedDependency_TreatsDependencyAsSatisfied()
    {
        var policy = WorkflowPolicy.Default with { MaxConcurrentSteps = 1 };
        WorkStepSnapshot[] steps =
        [
            Step("optional", WorkStepLifecycleStatus.Skipped),
            Step("child", WorkStepLifecycleStatus.Pending, dependsOn: ["optional"])
        ];

        var ready = WorkStepScheduler.SelectReadySteps(steps, policy);

        var onlyStep = Assert.ContainsSingle(ready);
        Assert.AreEqual("child", onlyStep.StepId);
    }

    private static WorkStepSnapshot Step(
        string stepId,
        WorkStepLifecycleStatus status,
        int depth = 0,
        string? parentStepId = null,
        string[]? dependsOn = null) =>
        new(
            stepId,
            "1",
            "session-1",
            "run-1",
            "worker",
            $"Goal for {stepId}",
            status,
            $"hash-{stepId}",
            depth,
            parentStepId,
            DependsOn: dependsOn);
}
