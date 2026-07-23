using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Modes.Work;

/// <summary>Selects runnable work steps under dependency and concurrency constraints.</summary>
public static class WorkStepScheduler
{
    public static IReadOnlyList<WorkStepSnapshot> SelectReadySteps(
        IReadOnlyList<WorkStepSnapshot> steps,
        WorkflowPolicy policy,
        int currentlyRunningCount = 0)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        var byId = steps.ToDictionary(static s => s.StepId, StringComparer.Ordinal);
        var completed = steps
            .Where(static s => s.Status is WorkStepLifecycleStatus.Completed
                or WorkStepLifecycleStatus.Skipped)
            .Select(static s => s.StepId)
            .ToHashSet(StringComparer.Ordinal);

        var capacity = Math.Max(0, policy.MaxConcurrentSteps - currentlyRunningCount);
        if (capacity == 0)
        {
            return [];
        }

        var ready = new List<WorkStepSnapshot>();
        foreach (var step in steps
            .Where(static s => s.Status == WorkStepLifecycleStatus.Pending)
            .OrderBy(static s => s.Depth)
            .ThenBy(static s => s.StepId, StringComparer.Ordinal))
        {
            var deps = step.DependsOn ?? [];
            if (deps.Any(dep => !completed.Contains(dep)))
            {
                continue;
            }

            if (step.ParentStepId is { Length: > 0 }
                && byId.TryGetValue(step.ParentStepId, out var parent)
                && parent.Status is not (WorkStepLifecycleStatus.Completed
                    or WorkStepLifecycleStatus.Skipped
                    or WorkStepLifecycleStatus.Running
                    or WorkStepLifecycleStatus.Pending))
            {
                // Parent failed/interrupted: skip unless failure policy handled elsewhere.
                continue;
            }

            ready.Add(step);
            if (ready.Count >= capacity)
            {
                break;
            }
        }

        return ready;
    }
}
