using Microsoft.Extensions.Logging.Abstractions;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI.Tests.Chat;

[TestClass]
public sealed class ChatGenerationContextBuilderTests
{
    private string _root = null!;
    private string _dbPath = null!;
    private CortanaDbContext _db = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortana-generation-context-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "context.db");
        Directory.CreateDirectory(_root);
        _db = new CortanaDbContext(_dbPath);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();

        try
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    Directory.Delete(_root, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 19)
                {
                    Thread.Sleep(100);
                }
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不应覆盖断言结果。
        }
    }

    [TestMethod]
    public void BuildPrompt_WhenRecentMessagesAndAssetsExist_IncludesContextSections()
    {
        var sessionId = "session-1";
        SeedMessage(sessionId, "user", "用户", "上一版图标太复杂了，请简化线条。", 1);
        SeedMessage(sessionId, "assistant", "设计助手", "收到，我会保留主体并简化线条。", 2);
        SeedAsset(sessionId, "assistant-1", "images", "上一版.png", Path.Combine("histories", sessionId, "images", "assistant-1", "上一版.png"), 1);

        var builder = new ChatGenerationContextBuilder(
            _db,
            new TestAppPaths(_root),
            NullLogger<ChatGenerationContextBuilder>.Instance);
        var agent = new AgentEntity
        {
            Id = "agent-1",
            Name = "视觉助手",
            Description = "擅长极简图标与品牌延展。",
            Instructions = "优先保留主体和识别度。"
        };

        var prompt = builder.BuildPrompt("把配色改成偏暖的橙色。", sessionId, "图片", agent);

        StringAssert.Contains(prompt, "你正在为 Cortana 的「视觉助手」执行图片生成任务。");
        StringAssert.Contains(prompt, "## 最近对话上下文（从旧到新）");
        StringAssert.Contains(prompt, "用户: 上一版图标太复杂了，请简化线条。");
        StringAssert.Contains(prompt, "设计助手: 收到，我会保留主体并简化线条。");
        StringAssert.Contains(prompt, "## 最近生成资源");
        StringAssert.Contains(prompt, "上一版.png");
        StringAssert.Contains(prompt, "## 本轮用户要求");
        StringAssert.Contains(prompt, "把配色改成偏暖的橙色。");
    }

    private void SeedMessage(string sessionId, string role, string authorName, string content, long createdTimestamp)
    {
        _db.Execute(
            """
            INSERT INTO ChatMessages
                (Id, CreatedTimestamp, UpdatedTimestamp, SessionId, Role, AuthorName, Content, ContentsJson, TokenCount, ModelName, CreatedAt, AgentId, AgentName)
            VALUES
                (@Id, @CreatedTimestamp, @UpdatedTimestamp, @SessionId, @Role, @AuthorName, @Content, @ContentsJson, @TokenCount, @ModelName, @CreatedAt, @AgentId, @AgentName)
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
                cmd.Parameters.AddWithValue("@CreatedTimestamp", createdTimestamp);
                cmd.Parameters.AddWithValue("@UpdatedTimestamp", createdTimestamp);
                cmd.Parameters.AddWithValue("@SessionId", sessionId);
                cmd.Parameters.AddWithValue("@Role", role);
                cmd.Parameters.AddWithValue("@AuthorName", authorName);
                cmd.Parameters.AddWithValue("@Content", content);
                cmd.Parameters.AddWithValue("@ContentsJson", string.Empty);
                cmd.Parameters.AddWithValue("@TokenCount", 0);
                cmd.Parameters.AddWithValue("@ModelName", "test-model");
                cmd.Parameters.AddWithValue("@CreatedAt", DateTimeOffset.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@AgentId", "agent-1");
                cmd.Parameters.AddWithValue("@AgentName", authorName);
            });
    }

    private void SeedAsset(string sessionId, string messageId, string assetGroup, string originalName, string relativePath, int sortOrder)
    {
        _db.Execute(
            """
            INSERT INTO ChatMessageAssets
                (Id, CreatedTimestamp, UpdatedTimestamp, SessionId, MessageId, Role, AssetGroup, AssetKind, MimeType,
                 OriginalName, RelativePath, FileSizeBytes, Sha256, SortOrder, Width, Height, DurationMs, SourceType, Status)
            VALUES
                (@Id, @CreatedTimestamp, @UpdatedTimestamp, @SessionId, @MessageId, @Role, @AssetGroup, @AssetKind, @MimeType,
                 @OriginalName, @RelativePath, @FileSizeBytes, @Sha256, @SortOrder, @Width, @Height, @DurationMs, @SourceType, @Status)
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
                cmd.Parameters.AddWithValue("@CreatedTimestamp", 10);
                cmd.Parameters.AddWithValue("@UpdatedTimestamp", 10);
                cmd.Parameters.AddWithValue("@SessionId", sessionId);
                cmd.Parameters.AddWithValue("@MessageId", messageId);
                cmd.Parameters.AddWithValue("@Role", "assistant");
                cmd.Parameters.AddWithValue("@AssetGroup", assetGroup);
                cmd.Parameters.AddWithValue("@AssetKind", "generated");
                cmd.Parameters.AddWithValue("@MimeType", "image/png");
                cmd.Parameters.AddWithValue("@OriginalName", originalName);
                cmd.Parameters.AddWithValue("@RelativePath", relativePath);
                cmd.Parameters.AddWithValue("@FileSizeBytes", 123L);
                cmd.Parameters.AddWithValue("@Sha256", "abc");
                cmd.Parameters.AddWithValue("@SortOrder", sortOrder);
                cmd.Parameters.AddWithValue("@Width", 0);
                cmd.Parameters.AddWithValue("@Height", 0);
                cmd.Parameters.AddWithValue("@DurationMs", 0L);
                cmd.Parameters.AddWithValue("@SourceType", "generated");
                cmd.Parameters.AddWithValue("@Status", "active");
            });
    }

    private sealed class TestAppPaths(string root) : IAppPaths
    {
        public string WorkspaceDirectory => Path.Combine(root, "workspace");
        public string UserDataDirectory => Path.Combine(root, "data");
        public string WorkspaceSkillsDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "skills");
        public string WorkspacePluginsDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "plugins");
        public string UserSkillsDirectory => Path.Combine(UserDataDirectory, "skills");
        public string UserPluginsDirectory => Path.Combine(UserDataDirectory, "plugins");
        public string UserAgentsDirectory => Path.Combine(UserDataDirectory, "agents");
        public string UserSolutionsDirectory => Path.Combine(UserDataDirectory, "solutions");
        public string PluginDirectory => Path.Combine(UserDataDirectory, "plugins-bin");
        public string WorkspaceResourcesDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "resources");
        public string HistoryResourcesDirectory => Path.Combine(WorkspaceResourcesDirectory, "histories");
        public string PromptsDirectory => Path.Combine(UserDataDirectory, "prompts");
    }
}
