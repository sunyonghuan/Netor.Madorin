using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.AI.MeetingMode;
using Netor.Cortana.Entitys;

using MeetingParticipant = Netor.Cortana.Entitys.MeetingParticipantDto;

namespace Netor.Cortana.MeetingMode.Tests;

[TestClass]
public sealed class MeetingAttachmentPathTests
{
    private string _root = null!;
    private string _source = null!;
    private TestAppPaths _paths = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortana-meeting-attachments-{Guid.NewGuid():N}");
        _source = Path.Combine(_root, "source");
        Directory.CreateDirectory(_source);
        _paths = new TestAppPaths(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void Import_CopiesFileToResources_AndReturnsRelativePath()
    {
        var sourceFile = Path.Combine(_source, "需求.txt");
        File.WriteAllText(sourceFile, "会议附件内容");
        var importer = new MeetingAttachmentImporter(_paths);

        var imported = importer.Import(
            "meeting-a",
            new AttachmentInfo(sourceFile, "需求.txt", "text/plain"));

        Assert.AreEqual("需求.txt", imported.FileName);
        Assert.IsFalse(Path.IsPathRooted(imported.RelativePath));
        StringAssert.StartsWith(imported.RelativePath, Path.Combine("meetings", "meeting-a", "attachments"));
        Assert.IsTrue(File.Exists(Path.Combine(_paths.WorkspaceResourcesDirectory, imported.RelativePath)));
        Assert.AreEqual(new FileInfo(sourceFile).Length, imported.SizeBytes);
    }

    [TestMethod]
    public void BuildOpeningMessageText_ExposesAbsoluteAttachmentPath()
    {
        var attachment = new MeetingAttachmentEntity
        {
            MeetingId = "meeting-b",
            FileName = "材料.md",
            StoredPath = Path.Combine("meetings", "meeting-b", "attachments", "材料.md"),
            MimeType = "text/markdown",
            SizeBytes = 42,
        };

        var text = MeetingAgentBuilder.BuildOpeningMessageText(
            CreateSession("meeting-b"),
            [new MeetingParticipant("agent-a", "专家A", 0)],
            [attachment],
            _paths.WorkspaceResourcesDirectory);

        StringAssert.Contains(text, Path.Combine(_paths.WorkspaceResourcesDirectory, attachment.StoredPath));
        StringAssert.Contains(text, "材料.md");
        StringAssert.Contains(text, "text/markdown");
    }

    [TestMethod]
    public void BuildOpeningMessageText_TruncatesAttachmentsAfterTen()
    {
        var attachments = Enumerable.Range(0, 12)
            .Select(index => new MeetingAttachmentEntity
            {
                MeetingId = "meeting-c",
                FileName = $"材料{index}.txt",
                StoredPath = Path.Combine("meetings", "meeting-c", "attachments", $"材料{index}.txt"),
                MimeType = "text/plain",
                SizeBytes = index,
            })
            .ToList();

        var text = MeetingAgentBuilder.BuildOpeningMessageText(
            CreateSession("meeting-c"),
            [new MeetingParticipant("agent-a", "专家A", 0)],
            attachments,
            _paths.WorkspaceResourcesDirectory);

        StringAssert.Contains(text, "材料9.txt");
        Assert.IsFalse(text.Contains("材料10.txt", StringComparison.Ordinal));
        StringAssert.Contains(text, "等共 12 个文件，可使用 list_meeting_attachments 查询完整清单");
    }

    private static MeetingSessionEntity CreateSession(string meetingId)
    {
        return new MeetingSessionEntity
        {
            Id = meetingId,
            SessionId = "session-test",
            Topic = "附件路径测试",
            ParticipantsJson = "[]",
        };
    }

    private sealed class TestAppPaths(string root) : IAppPaths
    {
        public string WorkspaceDirectory { get; } = Path.Combine(root, "workspace");

        public string UserDataDirectory { get; } = Path.Combine(root, "user");

        public string WorkspaceSkillsDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "skills");

        public string WorkspacePluginsDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "plugins");

        public string UserSkillsDirectory => Path.Combine(UserDataDirectory, "skills");

        public string UserPluginsDirectory => Path.Combine(UserDataDirectory, "plugins");

        public string UserAgentsDirectory => Path.Combine(UserDataDirectory, "agents");

        public string UserSolutionsDirectory => Path.Combine(UserDataDirectory, "solutions");

        public string PluginDirectory => UserPluginsDirectory;

        public string WorkspaceResourcesDirectory { get; } = Path.Combine(root, "workspace", ".cortana", "resources");

        public string HistoryResourcesDirectory => Path.Combine(WorkspaceResourcesDirectory, "histories");

        public string PromptsDirectory => Path.Combine(UserDataDirectory, "prompts");
    }
}
