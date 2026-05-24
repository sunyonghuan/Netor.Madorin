using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.AI.TaskEngine.Models;
using Netor.Cortana.AI.TaskEngine.Scheduling;

namespace Netor.Cortana.AI.Tests.TaskEngine;

[TestClass]
public sealed class StepSchedulerTests
{
    private StepScheduler _scheduler = null!;

    [TestInitialize]
    public void Setup() => _scheduler = new StepScheduler();

    // ══════════════════════════════════════════════════════════════════════
    // GetReadySteps
    // ══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void GetReadySteps_EmptyPlan_ReturnsEmpty()
    {
        var plan = CreatePlan();
        var ready = _scheduler.GetReadySteps(plan);
        Assert.AreEqual(0, ready.Count);
    }

    [TestMethod]
    public void GetReadySteps_SinglePendingStep_NoDeps_ReturnsIt()
    {
        var plan = CreatePlan(
            new PlanStep { StepId = "s1", Sequence = 1, Title = "Step 1", Status = PlanStepStatus.Pending });

        var ready = _scheduler.GetReadySteps(plan);

        Assert.AreEqual(1, ready.Count);
        Assert.AreEqual("s1", ready[0].StepId);
    }

    [TestMethod]
    public void GetReadySteps_StepWithUnmetDeps_TransitionsToWaitingDeps()
    {
        var plan = CreatePlan(
            new PlanStep { StepId = "s1", Sequence = 1, Title = "Step 1", Status = PlanStepStatus.Running },
            new PlanStep { StepId = "s2", Sequence = 2, Title = "Step 2", Status = PlanStepStatus.Pending, DependsOn = ["s1"] });

        var ready = _scheduler.GetReadySteps(plan);

        Assert.AreEqual(0, ready.Count);
        Assert.AreEqual(PlanStepStatus.WaitingDeps, plan.Steps[1].Status);
    }

    [TestMethod]
    public void GetReadySteps_DepsCompleted_ReturnsDependent()
    {
        var plan = CreatePlan(
            new PlanStep { StepId = "s1", Sequence = 1, Title = "Step 1", Status = PlanStepStatus.Completed },
            new PlanStep { StepId = "s2", Sequence = 2, Title = "Step 2", Status = PlanStepStatus.Pending, DependsOn = ["s1"] });

        var ready = _scheduler.GetReadySteps(plan);

        Assert.AreEqual(1, ready.Count);
        Assert.AreEqual("s2", ready[0].StepId);
    }

    [TestMethod]
    public void GetReadySteps_DepsSkipped_ReturnsDependent()
    {
        var plan = CreatePlan(
            new PlanStep { StepId = "s1", Sequence = 1, Title = "Step 1", Status = PlanStepStatus.Skipped },
            new PlanStep { StepId = "s2", Sequence = 2, Title = "Step 2", Status = PlanStepStatus.Pending, DependsOn = ["s1"] });

        var ready = _scheduler.GetReadySteps(plan);

        Assert.AreEqual(1, ready.Count);
        Assert.AreEqual("s2", ready[0].StepId);
    }

    [TestMethod]
    public void GetReadySteps_AwaitUserMode_TransitionsToWaitingUser()
    {
        var plan = CreatePlan(
            new PlanStep { StepId = "s1", Sequence = 1, Title = "Step 1", Status = PlanStepStatus.Pending, ExecutionMode = "await_user" });

        var ready = _scheduler.GetReadySteps(plan);

        Assert.AreEqual(0, ready.Count);
        Assert.AreEqual(PlanStepStatus.WaitingUser, plan.Steps[0].Status);
    }

    [TestMethod]
    public void GetReadySteps_MultipleParallelSteps_ReturnsAll()
    {
        var plan = CreatePlan(
            new PlanStep { StepId = "s1", Sequence = 1, Title = "Step 1", Status = PlanStepStatus.Pending },
            new PlanStep { StepId = "s2", Sequence = 2, Title = "Step 2", Status = PlanStepStatus.Pending },
            new PlanStep { StepId = "s3", Sequence = 3, Title = "Step 3", Status = PlanStepStatus.Pending });

        var ready = _scheduler.GetReadySteps(plan);

        Assert.AreEqual(3, ready.Count);
    }

    [TestMethod]
    public void GetReadySteps_CompletedStep_NotReturned()
    {
        var plan = CreatePlan(
            new PlanStep { StepId = "s1", Sequence = 1, Title = "Step 1", Status = PlanStepStatus.Completed },
            new PlanStep { StepId = "s2", Sequence = 2, Title = "Step 2", Status = PlanStepStatus.Pending });

        var ready = _scheduler.GetReadySteps(plan);

        Assert.AreEqual(1, ready.Count);
        Assert.AreEqual("s2", ready[0].StepId);
    }

    [TestMethod]
    public void GetReadySteps_WaitingDepsStep_BecomesReadyWhenDepsComplete()
    {
        var plan = CreatePlan(
            new PlanStep { StepId = "s1", Sequence = 1, Title = "Step 1", Status = PlanStepStatus.Completed },
            new PlanStep { StepId = "s2", Sequence = 2, Title = "Step 2", Status = PlanStepStatus.WaitingDeps, DependsOn = ["s1"] });

        var ready = _scheduler.GetReadySteps(plan);

        Assert.AreEqual(1, ready.Count);
        Assert.AreEqual("s2", ready[0].StepId);
    }

    // ══════════════════════════════════════════════════════════════════════
    // IsAllCompleted
    // ══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void IsAllCompleted_EmptyPlan_ReturnsFalse()
    {
        var plan = CreatePlan();
        Assert.IsFalse(_scheduler.IsAllCompleted(plan));
    }

    [TestMethod]
    public void IsAllCompleted_AllCompleted_ReturnsTrue()
    {
        var plan = CreatePlan(
            new PlanStep { StepId = "s1", Sequence = 1, Title = "Step 1", Status = PlanStepStatus.Completed },
            new PlanStep { StepId = "s2", Sequence = 2, Title = "Step 2", Status = PlanStepStatus.Completed });

        Assert.IsTrue(_scheduler.IsAllCompleted(plan));
    }

    [TestMethod]
    public void IsAllCompleted_MixedCompletedSkippedCancelled_ReturnsTrue()
    {
        var plan = CreatePlan(
            new PlanStep { StepId = "s1", Sequence = 1, Title = "Step 1", Status = PlanStepStatus.Completed },
            new PlanStep { StepId = "s2", Sequence = 2, Title = "Step 2", Status = PlanStepStatus.Skipped },
            new PlanStep { StepId = "s3", Sequence = 3, Title = "Step 3", Status = PlanStepStatus.Cancelled });

        Assert.IsTrue(_scheduler.IsAllCompleted(plan));
    }

    [TestMethod]
    public void IsAllCompleted_HasPendingStep_ReturnsFalse()
    {
        var plan = CreatePlan(
            new PlanStep { StepId = "s1", Sequence = 1, Title = "Step 1", Status = PlanStepStatus.Completed },
            new PlanStep { StepId = "s2", Sequence = 2, Title = "Step 2", Status = PlanStepStatus.Pending });

        Assert.IsFalse(_scheduler.IsAllCompleted(plan));
    }

    [TestMethod]
    public void IsAllCompleted_HasRunningStep_ReturnsFalse()
    {
        var plan = CreatePlan(
            new PlanStep { StepId = "s1", Sequence = 1, Title = "Step 1", Status = PlanStepStatus.Running });

        Assert.IsFalse(_scheduler.IsAllCompleted(plan));
    }

    // ══════════════════════════════════════════════════════════════════════
    // IsWaitingUser / HasFailedSteps
    // ══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void IsWaitingUser_NoWaitingSteps_ReturnsFalse()
    {
        var plan = CreatePlan(
            new PlanStep { StepId = "s1", Sequence = 1, Title = "Step 1", Status = PlanStepStatus.Running });

        Assert.IsFalse(_scheduler.IsWaitingUser(plan));
    }

    [TestMethod]
    public void IsWaitingUser_HasWaitingUserStep_ReturnsTrue()
    {
        var plan = CreatePlan(
            new PlanStep { StepId = "s1", Sequence = 1, Title = "Step 1", Status = PlanStepStatus.WaitingUser });

        Assert.IsTrue(_scheduler.IsWaitingUser(plan));
    }

    [TestMethod]
    public void HasFailedSteps_NoFailedSteps_ReturnsFalse()
    {
        var plan = CreatePlan(
            new PlanStep { StepId = "s1", Sequence = 1, Title = "Step 1", Status = PlanStepStatus.Completed });

        Assert.IsFalse(_scheduler.HasFailedSteps(plan));
    }

    [TestMethod]
    public void HasFailedSteps_HasFailedStep_ReturnsTrue()
    {
        var plan = CreatePlan(
            new PlanStep { StepId = "s1", Sequence = 1, Title = "Step 1", Status = PlanStepStatus.Failed });

        Assert.IsTrue(_scheduler.HasFailedSteps(plan));
    }

    // ══════════════════════════════════════════════════════════════════════
    // 辅助方法
    // ══════════════════════════════════════════════════════════════════════

    private static ExecutionPlan CreatePlan(params PlanStep[] steps)
    {
        return new ExecutionPlan
        {
            PlanId = "plan-test",
            TaskId = "task-test",
            Steps = steps.ToList(),
        };
    }
}
