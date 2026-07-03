using Microsoft.Data.Sqlite;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 工作流计划模板服务（WorkPlanTemplates 表）。
/// 支持"保存当前任务的计划为模板"和"从模板加载计划"。
/// </summary>
public sealed class WorkPlanTemplateService
{
    private readonly CortanaDbContext _db;

    public WorkPlanTemplateService(CortanaDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public WorkPlanTemplateEntity? GetById(string id)
    {
        return _db.QueryFirstOrDefault(
            "SELECT * FROM WorkPlanTemplates WHERE Id = @Id",
            ReadEntity,
            cmd => cmd.Parameters.AddWithValue("@Id", id));
    }

    /// <summary>在当前 workspace 可见范围内按 ID 获取模板。</summary>
    public WorkPlanTemplateEntity? GetVisibleById(string id, string? workspaceId)
    {
        return _db.QueryFirstOrDefault("""
            SELECT * FROM WorkPlanTemplates
            WHERE Id = @Id
              AND (Scope = 'system'
                   OR Scope = 'user'
                   OR (Scope = 'workspace' AND WorkspaceId = @Workspace))
            """,
            ReadEntity,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Id", id);
                cmd.Parameters.AddWithValue("@Workspace", (object?)workspaceId ?? DBNull.Value);
            });
    }

    /// <summary>按关键词 + 分类过滤搜索（按使用次数 + 最近使用倒序）。</summary>
    public List<WorkPlanTemplateEntity> Search(string? keyword, string? category, string? workspaceId, int take = 20)
    {
        return _db.Query("""
            SELECT * FROM WorkPlanTemplates
            WHERE (Scope = 'system'
                   OR Scope = 'user'
                   OR (Scope = 'workspace' AND WorkspaceId = @Workspace))
              AND (@Keyword IS NULL OR Name LIKE @KeywordLike OR Description LIKE @KeywordLike)
              AND (@Category IS NULL OR Category = @Category)
            ORDER BY UseCount DESC, IFNULL(LastUsedAt, 0) DESC
            LIMIT @Take
            """,
            ReadEntity,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Workspace", (object?)workspaceId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Keyword", (object?)keyword ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@KeywordLike", string.IsNullOrEmpty(keyword) ? DBNull.Value : $"%{keyword}%");
                cmd.Parameters.AddWithValue("@Category", (object?)category ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Take", take);
            });
    }

    /// <summary>按名称模糊匹配，返回最匹配的一条（用于 AI 工具 load_plan_from_template）。</summary>
    public WorkPlanTemplateEntity? FindBestMatch(string nameOrId, string? workspaceId = null)
    {
        // 先按 ID 精确匹配
        var byId = GetVisibleById(nameOrId, workspaceId);
        if (byId is not null) return byId;

        // 再按名称 LIKE
        var matches = Search(nameOrId, category: null, workspaceId, take: 1);
        return matches.FirstOrDefault();
    }

    public void Create(WorkPlanTemplateEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        entity.CreatedAt = now;
        entity.UpdatedAt = now;
        _db.Execute(InsertSql, cmd => BindEntity(cmd, entity));
    }

    public void IncrementUseCount(string id)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute(
            "UPDATE WorkPlanTemplates SET UseCount = UseCount + 1, LastUsedAt = @Now, UpdatedAt = @Now WHERE Id = @Id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@Id", id);
            });
    }

    public void Delete(string id)
    {
        _db.Execute(
            "DELETE FROM WorkPlanTemplates WHERE Id = @Id",
            cmd => cmd.Parameters.AddWithValue("@Id", id));
    }

    private const string InsertSql = """
        INSERT INTO WorkPlanTemplates (
            Id, Name, Description, Category, PlanJson, SourceTaskId, SourceKind,
            UseCount, LastUsedAt, Scope, WorkspaceId, CreatedAt, UpdatedAt
        ) VALUES (
            @Id, @Name, @Description, @Category, @PlanJson, @SourceTaskId, @SourceKind,
            @UseCount, @LastUsedAt, @Scope, @WorkspaceId, @CreatedAt, @UpdatedAt
        )
        """;

    private static WorkPlanTemplateEntity ReadEntity(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("Id")),
        Name = r.GetString(r.GetOrdinal("Name")),
        Description = r.GetString(r.GetOrdinal("Description")),
        Category = r.GetString(r.GetOrdinal("Category")),
        PlanJson = r.GetString(r.GetOrdinal("PlanJson")),
        SourceTaskId = r.IsDBNull(r.GetOrdinal("SourceTaskId")) ? null : r.GetString(r.GetOrdinal("SourceTaskId")),
        SourceKind = r.IsDBNull(r.GetOrdinal("SourceKind")) ? null : r.GetString(r.GetOrdinal("SourceKind")),
        UseCount = r.GetInt64(r.GetOrdinal("UseCount")),
        LastUsedAt = r.IsDBNull(r.GetOrdinal("LastUsedAt")) ? null : r.GetInt64(r.GetOrdinal("LastUsedAt")),
        Scope = r.GetString(r.GetOrdinal("Scope")),
        WorkspaceId = r.IsDBNull(r.GetOrdinal("WorkspaceId")) ? null : r.GetString(r.GetOrdinal("WorkspaceId")),
        CreatedAt = r.GetInt64(r.GetOrdinal("CreatedAt")),
        UpdatedAt = r.GetInt64(r.GetOrdinal("UpdatedAt")),
    };

    private static void BindEntity(SqliteCommand cmd, WorkPlanTemplateEntity e)
    {
        cmd.Parameters.AddWithValue("@Id", e.Id);
        cmd.Parameters.AddWithValue("@Name", e.Name);
        cmd.Parameters.AddWithValue("@Description", e.Description);
        cmd.Parameters.AddWithValue("@Category", e.Category);
        cmd.Parameters.AddWithValue("@PlanJson", e.PlanJson);
        cmd.Parameters.AddWithValue("@SourceTaskId", (object?)e.SourceTaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@SourceKind", (object?)e.SourceKind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@UseCount", e.UseCount);
        cmd.Parameters.AddWithValue("@LastUsedAt", (object?)e.LastUsedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Scope", e.Scope);
        cmd.Parameters.AddWithValue("@WorkspaceId", (object?)e.WorkspaceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", e.CreatedAt);
        cmd.Parameters.AddWithValue("@UpdatedAt", e.UpdatedAt);
    }
}
