using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Server;

internal static class RuntimeRunMetadata
{
    public static string ComputeRequestHash(NewSessionRunRequest request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            request,
            RuntimeJsonContext.Default.NewSessionRunRequest);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static string ComputeRequestHash(ExistingSessionRunRequest request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            request,
            RuntimeJsonContext.Default.ExistingSessionRunRequest);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static NextTurnSelection RemovePromptContent(NextTurnSelection selection)
    {
        ModeOptions modeOptions = selection.ModeOptions switch
        {
            ExpertModeOptions expert => new ExpertModeOptions(RemovePromptContent(expert.Agent)),
            MeetingModeOptions meeting => meeting with
            {
                Participants = [.. meeting.Participants.Select(
                    static p => p with { Agent = RemovePromptContent(p.Agent) })],
                HostAgent = meeting.HostAgent is { } host
                    ? RemovePromptContent(host)
                    : null,
                SelectorPolicy = meeting.SelectorPolicy is { } sp && sp.SelectorAgent is { } sa
                    ? sp with { SelectorAgent = RemovePromptContent(sa) }
                    : meeting.SelectorPolicy,
                Policy = meeting.Policy is { } pol && pol.Summarizer is { } sum
                    ? pol with { Summarizer = RemovePromptContent(sum) }
                    : meeting.Policy,
            },
            WorkModeOptions work => new WorkModeOptions(
                RemovePromptContent(work.GeneralManager),
                [.. work.AvailableAgents.Select(RemovePromptContent)],
                work.WorkflowPolicy,
                work.ContextPolicy),
            _ => throw new InvalidOperationException(
                $"Mode options '{selection.ModeOptions.GetType().Name}' are not supported.")
        };
        return selection with { ModeOptions = modeOptions };
    }

    public static AgentSnapshot[] CreateAgentSnapshots(NextTurnSelection selection)
    {
        IEnumerable<AgentRef> agents = selection.ModeOptions switch
        {
            ExpertModeOptions expert => [expert.Agent],
            MeetingModeOptions meeting => EnumerateMeetingAgents(meeting),
            WorkModeOptions work => [work.GeneralManager, .. work.AvailableAgents],
            _ => throw new InvalidOperationException(
                $"Mode options '{selection.ModeOptions.GetType().Name}' are not supported.")
        };

        return
        [
            .. agents
                .DistinctBy(static a => a.AgentId, StringComparer.Ordinal)
                .OrderBy(static a => a.AgentId, StringComparer.Ordinal)
                .Select(static agent => new AgentSnapshot(
                    agent.AgentId,
                    agent.PromptTemplateVersion,
                    ComputePromptHash(agent.SystemPrompt),
                    agent.ProviderId,
                    agent.ModelId))
        ];
    }

    public static string ComputePromptHash(string prompt) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)));

    private static AgentRef RemovePromptContent(AgentRef agent) =>
        agent with { SystemPrompt = string.Empty };

    private static IEnumerable<AgentRef> EnumerateMeetingAgents(MeetingModeOptions meeting)
    {
        foreach (var p in meeting.Participants)
        {
            yield return p.Agent;
        }

        if (meeting.HostAgent is { } host)
        {
            yield return host;
        }

        if (meeting.SelectorPolicy?.SelectorAgent is { } selector)
        {
            yield return selector;
        }

        if (meeting.Policy?.Summarizer is { } summarizer)
        {
            yield return summarizer;
        }
    }
}
