using System.Text.Json.Serialization;

using Netor.Cortana.AI.Hitl.Models;
using Netor.Cortana.AI.MeetingMode.Models;
using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI.MeetingMode.Json;

/// <summary>
/// 会议模式 AOT JSON 源生成上下文。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(MeetingParticipantDto))]
[JsonSerializable(typeof(List<MeetingParticipantDto>))]
[JsonSerializable(typeof(MeetingToolCallRecord))]
[JsonSerializable(typeof(List<MeetingToolCallRecord>))]
[JsonSerializable(typeof(MeetingAttachmentRef))]
[JsonSerializable(typeof(List<MeetingAttachmentRef>))]
[JsonSerializable(typeof(MeetingTurnDecision))]
[JsonSerializable(typeof(MeetingExecutionPlan))]
[JsonSerializable(typeof(MeetingExecutionPhase))]
[JsonSerializable(typeof(List<MeetingExecutionPhase>))]
[JsonSerializable(typeof(HitlPendingRequestSnapshot))]
public partial class MeetingJsonContext : JsonSerializerContext;
