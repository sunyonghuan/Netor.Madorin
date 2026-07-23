using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Modes.Work;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
public sealed class WorkPlanValidatorTests
{
    [TestMethod]
    public void ValidateOrThrow_WithValidDependencyPlan_ReturnsPlan()
    {
        var options = CreateOptions();
        var plan = new WorkPlanDraft(
            "1",
            "Ship release",
            [
                new WorkPlanStepDraft("step-a", "Prepare notes", "worker"),
                new WorkPlanStepDraft("step-b", "Review notes", "reviewer", DependsOn: ["step-a"])
            ]);

        var validated = WorkPlanValidator.ValidateOrThrow(plan, options);

        Assert.AreSame(plan, validated);
    }

    [TestMethod]
    public void ValidateOrThrow_WithUnknownAgent_ThrowsBeforeExecution()
    {
        var plan = new WorkPlanDraft(
            "1",
            "Ship release",
            [new WorkPlanStepDraft("step-a", "Prepare notes", "missing-agent")]);

        Assert.ThrowsExactly<ArgumentException>(
            () => WorkPlanValidator.ValidateOrThrow(plan, CreateOptions()));
    }

    [TestMethod]
    public void ValidateOrThrow_WithEmptyPlan_ThrowsBeforeExecution()
    {
        var plan = new WorkPlanDraft("1", "Ship release", []);

        Assert.ThrowsExactly<ArgumentException>(
            () => WorkPlanValidator.ValidateOrThrow(plan, CreateOptions()));
    }

    [TestMethod]
    public void ValidateOrThrow_WithTooManySteps_ThrowsBeforeExecution()
    {
        var options = CreateOptions(
            new WorkflowPolicy(
                MaxStepsPerRun: 1,
                HardMaxStepsPerRun: 2));
        var plan = new WorkPlanDraft(
            "1",
            "Ship release",
            [
                new WorkPlanStepDraft("step-a", "Prepare notes", "worker"),
                new WorkPlanStepDraft("step-b", "Review notes", "reviewer", DependsOn: ["step-a"])
            ]);

        Assert.ThrowsExactly<ArgumentException>(
            () => WorkPlanValidator.ValidateOrThrow(plan, options));
    }

    [TestMethod]
    public void ValidateOrThrow_WithDependencyCycle_ThrowsBeforeExecution()
    {
        var plan = new WorkPlanDraft(
            "1",
            "Ship release",
            [
                new WorkPlanStepDraft("step-a", "Prepare notes", "worker", DependsOn: ["step-b"]),
                new WorkPlanStepDraft("step-b", "Review notes", "reviewer", DependsOn: ["step-a"])
            ]);

        Assert.ThrowsExactly<ArgumentException>(
            () => WorkPlanValidator.ValidateOrThrow(plan, CreateOptions()));
    }

    [TestMethod]
    public void ValidateOrThrow_WithDepthBeyondPolicy_ThrowsBeforeExecution()
    {
        var options = CreateOptions(new WorkflowPolicy(MaxAgentDepth: 1));
        var plan = new WorkPlanDraft(
            "1",
            "Ship release",
            [new WorkPlanStepDraft("step-a", "Prepare notes", "worker", Depth: 2)]);

        Assert.ThrowsExactly<ArgumentException>(
            () => WorkPlanValidator.ValidateOrThrow(plan, options));
    }

    [TestMethod]
    public void ValidateOrThrow_WithSelfParent_ThrowsBeforeExecution()
    {
        var plan = new WorkPlanDraft(
            "1",
            "Ship release",
            [
                new WorkPlanStepDraft(
                    "step-a",
                    "Prepare notes",
                    "worker",
                    ParentStepId: "step-a")
            ]);

        Assert.ThrowsExactly<ArgumentException>(
            () => WorkPlanValidator.ValidateOrThrow(plan, CreateOptions()));
    }

    private static WorkModeOptions CreateOptions(WorkflowPolicy? policy = null)
    {
        var manager = new AgentRef(
            "manager",
            "v1",
            "Plan the work.",
            "fake",
            "test-model");
        var worker = new AgentRef(
            "worker",
            "v1",
            "Execute work.",
            "fake",
            "test-model");
        var reviewer = new AgentRef(
            "reviewer",
            "v1",
            "Review work.",
            "fake",
            "test-model");
        return new WorkModeOptions(manager, [worker, reviewer], policy);
    }
}
