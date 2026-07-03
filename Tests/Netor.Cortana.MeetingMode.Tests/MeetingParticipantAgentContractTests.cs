using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.AI.MeetingMode;
using Netor.Cortana.Entitys;

namespace Netor.Cortana.MeetingMode.Tests;

[TestClass]
public sealed class MeetingParticipantAgentContractTests
{
    [TestMethod]
    public void CloneParticipantForMeeting_PrependsHeader_AndPreservesOriginalInstructions()
    {
        var source = new AgentEntity
        {
            Id = "agent-a",
            Name = "专家A",
            Instructions = "用户提问时直接回答。",
            BoundPlugins = ["plugin-a"],
            BoundMcp = ["mcp-a"],
        };

        var cloned = MeetingAgentBuilder.CloneParticipantForMeeting(
            source,
            "【当前模式：会议模式】请围绕会议主题发表观点。");

        Assert.StartsWith("【当前模式：会议模式】", cloned.Instructions);
        Assert.Contains("用户提问时直接回答。", cloned.Instructions);
        Assert.Contains("\n\n---\n\n", cloned.Instructions);
        CollectionAssert.AreEqual(source.BoundPlugins, cloned.BoundPlugins);
        CollectionAssert.AreEqual(source.BoundMcp, cloned.BoundMcp);
        Assert.AreNotSame(source.BoundPlugins, cloned.BoundPlugins);
        Assert.AreNotSame(source.BoundMcp, cloned.BoundMcp);
    }

    [TestMethod]
    public void ParticipantMeetingToolNames_ContainsAttachmentQueryOnly_AndExcludesAskUser()
    {
        var toolNames = MeetingAgentBuilder.ParticipantMeetingToolNames;

        Assert.HasCount(1, toolNames);
        Assert.AreEqual("list_meeting_attachments", toolNames[0]);
        Assert.DoesNotContain("ask_user", toolNames);
    }
}
