using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Contracts;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "mode")]
[JsonDerivedType(typeof(ExpertModeOptions), "expert")]
[JsonDerivedType(typeof(MeetingModeOptions), "meeting")]
[JsonDerivedType(typeof(WorkModeOptions), "work")]
public abstract record ModeOptions;

public sealed record ExpertModeOptions(AgentRef Agent) : ModeOptions;

/// <summary>Controls how the next speaker is chosen each round.</summary>
public sealed record MeetingSelectorPolicyOptions(
    MeetingSelectorPolicy Type = MeetingSelectorPolicy.SelectorDriven,
    AgentRef? SelectorAgent = null,
    int? MaxRounds = null,
    bool AllowStandbyActivation = true);

/// <summary>
/// Meeting governance: summary, termination, HITL, timeouts, retries and failure handling.
/// </summary>
public sealed record MeetingPolicy(
    MeetingSummaryMode SummaryMode = MeetingSummaryMode.None,
    AgentRef? Summarizer = null,
    MeetingConclusionCondition ConcludeCondition = MeetingConclusionCondition.HostDecides,
    string[]? TerminationConditions = null,
    bool HitlEnabled = false,
    int? HitlTimeoutSeconds = null,
    int? MeetingTimeoutSeconds = null,
    int InvocationTimeoutSeconds = 300,
    bool AllowLateJoin = true,
    int MaxRetriesPerInvocation = 1,
    MeetingParticipantFailurePolicy ParticipantFailure = MeetingParticipantFailurePolicy.Skip,
    MeetingHostFailurePolicy HostFailure = MeetingHostFailurePolicy.Pause,
    MeetingSelectorFailurePolicy SelectorFailure = MeetingSelectorFailurePolicy.DeterministicFallback,
    MeetingSummarizerFailurePolicy SummarizerFailure = MeetingSummarizerFailurePolicy.FallbackTailWindow);

/// <summary>
/// How the canonical history is projected into each meeting role's context.
/// </summary>
public sealed record MeetingContextPolicy(
    MeetingContextStrategy Strategy = MeetingContextStrategy.Full,
    int? MaxTokens = null,
    int? TailMessageCount = null,
    int? SlidingRoundCount = null,
    bool PreserveCurrentRound = true,
    bool PreserveToolResults = true,
    bool PreserveReasoning = false,
    MeetingSummaryFallbackStrategy SummaryFailureFallback =
        MeetingSummaryFallbackStrategy.TailWindow);

/// <summary>
/// Meeting mode configuration with participants, roles, and nested policy objects.
/// </summary>
public sealed record MeetingModeOptions : ModeOptions
{
    public MeetingParticipant[] Participants { get; init; }

    public AgentRef? HostAgent { get; init; }

    public MeetingSelectorPolicyOptions? SelectorPolicy { get; init; }

    public MeetingPolicy? Policy { get; init; }

    public MeetingContextPolicy? ContextPolicy { get; init; }

    [JsonIgnore]
    public MeetingSelectorPolicyOptions EffectiveSelectorPolicy =>
        SelectorPolicy ?? new MeetingSelectorPolicyOptions();

    [JsonIgnore]
    public MeetingPolicy EffectivePolicy => Policy ?? new MeetingPolicy();

    [JsonIgnore]
    public MeetingContextPolicy EffectiveContextPolicy =>
        ContextPolicy ?? new MeetingContextPolicy();

    /// <summary>Legacy host identifier retained for wire compatibility.</summary>
    public string? HostAgentId { get; init; }

    [JsonConstructor]
    public MeetingModeOptions(
        MeetingParticipant[] participants,
        AgentRef? hostAgent = null,
        MeetingSelectorPolicyOptions? selectorPolicy = null,
        MeetingPolicy? policy = null,
        MeetingContextPolicy? contextPolicy = null,
        string? hostAgentId = null)
    {
        Participants = participants;
        HostAgent = hostAgent;
        SelectorPolicy = selectorPolicy;
        Policy = policy;
        ContextPolicy = contextPolicy;
        HostAgentId = hostAgentId;
    }

    /// <summary>Backward-compatible constructor that wraps bare <see cref="AgentRef"/> entries as participants.</summary>
    public MeetingModeOptions(AgentRef[] participants, string? hostAgentId = null)
        : this(
            participants.Select((a, i) =>
                MeetingParticipant.FromLegacy(a, i)).ToArray(),
            hostAgentId: hostAgentId)
    {
    }
}

/// <summary>
/// Work mode configuration with a general manager, child agents, and optional policies.
/// </summary>
public sealed record WorkModeOptions : ModeOptions
{
    public AgentRef GeneralManager { get; init; }

    public AgentRef[] AvailableAgents { get; init; }

    public WorkflowPolicy? WorkflowPolicy { get; init; }

    public WorkContextPolicy? ContextPolicy { get; init; }

    [JsonIgnore]
    public WorkflowPolicy EffectiveWorkflowPolicy =>
        WorkflowPolicy ?? Contracts.WorkflowPolicy.Default;

    [JsonIgnore]
    public WorkContextPolicy EffectiveContextPolicy =>
        ContextPolicy ?? WorkContextPolicy.Default;

    [JsonConstructor]
    public WorkModeOptions(
        AgentRef generalManager,
        AgentRef[] availableAgents,
        WorkflowPolicy? workflowPolicy = null,
        WorkContextPolicy? contextPolicy = null)
    {
        GeneralManager = generalManager;
        AvailableAgents = availableAgents;
        WorkflowPolicy = workflowPolicy;
        ContextPolicy = contextPolicy;
    }
}
