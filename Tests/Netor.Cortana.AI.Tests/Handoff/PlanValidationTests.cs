using System.Text.Json;

using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.AI.WorkMode.Tools;

namespace Netor.Cortana.AI.Tests.Handoff;

[TestClass]
public sealed class PlanValidationTests
{
    [TestMethod]
    public void ValidatePlan_ReturnsError_WhenAcceptanceCriteriaMissing()
    {
        const string planJson = """
            {"main_steps":[{"title":"实现功能","sub_steps":[{"title":"接入工具","acceptance_criteria":""}]}]}
            """;

        var plan = JsonSerializer.Deserialize(planJson, WorkModeJsonContext.Default.WorkPlan);
        var error = PlanTools.ValidatePlan(plan);

        Assert.IsNotNull(error);
        StringAssert.Contains(error, "acceptance_criteria");
    }

    [TestMethod]
    public void ValidatePlan_ReturnsNull_WhenPlanIsComplete()
    {
        const string planJson = """
            {"main_steps":[{"title":"实现功能","sub_steps":[{"title":"接入工具","acceptance_criteria":"工具能创建工作任务并进入工作模式"}]}]}
            """;

        var plan = JsonSerializer.Deserialize(planJson, WorkModeJsonContext.Default.WorkPlan);
        var error = PlanTools.ValidatePlan(plan);

        Assert.IsNull(error);
    }
}
