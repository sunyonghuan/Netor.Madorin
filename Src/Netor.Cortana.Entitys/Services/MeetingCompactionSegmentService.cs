using Microsoft.Data.Sqlite;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 会议模式上下文压缩段数据服务（MeetingCompactionSegments 表）。
/// </summary>
public sealed class MeetingCompactionSegmentService
{
    private readonly CortanaDbContext _db;

    public MeetingCompactionSegmentService(CortanaDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>新增压缩段。</summary>
    public string Add(MeetingCompactionSegmentEntity segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        if (string.IsNullOrWhiteSpace(segment.Id))
        {
            segment.Id = Guid.NewGuid().ToString("N");
        }

        segment.CreatedAt = now;
        segment.UpdatedAt = now;

        _db.Execute("""
            INSERT INTO MeetingCompactionSegments (
                Id, MeetingId, SegmentIndex, StartSequence, EndSequence, Summary,
                OriginalMessageCount, ModelName, CreatedAt, UpdatedAt
            ) VALUES (
                @Id, @MeetingId, @SegmentIndex, @StartSequence, @EndSequence, @Summary,
                @OriginalMessageCount, @ModelName, @CreatedAt, @UpdatedAt
            )
            """,
            cmd => BindEntity(cmd, segment));
        return segment.Id;
    }

    /// <summary>按段落序号列出会议压缩段。</summary>
    public List<MeetingCompactionSegmentEntity> GetByMeetingId(string meetingId)
    {
        return _db.Query("""
            SELECT * FROM MeetingCompactionSegments
            WHERE MeetingId = @MeetingId
            ORDER BY SegmentIndex ASC
            """,
            ReadEntity,
            cmd => cmd.Parameters.AddWithValue("@MeetingId", meetingId));
    }

    /// <summary>在指定事务内按段落序号列出会议压缩段。</summary>
    public List<MeetingCompactionSegmentEntity> GetByMeetingId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string meetingId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT * FROM MeetingCompactionSegments
            WHERE MeetingId = @MeetingId
            ORDER BY SegmentIndex ASC
            """;
        command.Parameters.AddWithValue("@MeetingId", meetingId);

        using var reader = command.ExecuteReader();
        var result = new List<MeetingCompactionSegmentEntity>();
        while (reader.Read())
        {
            result.Add(ReadEntity(reader));
        }

        return result;
    }

    /// <summary>获取最大段落序号，无段落返回 -1。</summary>
    public int GetMaxSegmentIndex(string meetingId)
    {
        return _db.ExecuteScalar<int>(
            "SELECT IFNULL(MAX(SegmentIndex), -1) FROM MeetingCompactionSegments WHERE MeetingId = @MeetingId",
            cmd => cmd.Parameters.AddWithValue("@MeetingId", meetingId));
    }

    /// <summary>获取已压缩覆盖的最大消息序号，无段落返回 -1。</summary>
    public int GetMaxEndSequence(string meetingId)
    {
        return _db.ExecuteScalar<int>(
            "SELECT IFNULL(MAX(EndSequence), -1) FROM MeetingCompactionSegments WHERE MeetingId = @MeetingId",
            cmd => cmd.Parameters.AddWithValue("@MeetingId", meetingId));
    }

    private static MeetingCompactionSegmentEntity ReadEntity(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("Id")),
        MeetingId = r.GetString(r.GetOrdinal("MeetingId")),
        SegmentIndex = r.GetInt32(r.GetOrdinal("SegmentIndex")),
        StartSequence = r.GetInt32(r.GetOrdinal("StartSequence")),
        EndSequence = r.GetInt32(r.GetOrdinal("EndSequence")),
        Summary = r.GetString(r.GetOrdinal("Summary")),
        OriginalMessageCount = r.GetInt32(r.GetOrdinal("OriginalMessageCount")),
        ModelName = r.GetString(r.GetOrdinal("ModelName")),
        CreatedAt = r.GetInt64(r.GetOrdinal("CreatedAt")),
        UpdatedAt = r.GetInt64(r.GetOrdinal("UpdatedAt")),
    };

    private static void BindEntity(SqliteCommand cmd, MeetingCompactionSegmentEntity e)
    {
        cmd.Parameters.AddWithValue("@Id", e.Id);
        cmd.Parameters.AddWithValue("@MeetingId", e.MeetingId);
        cmd.Parameters.AddWithValue("@SegmentIndex", e.SegmentIndex);
        cmd.Parameters.AddWithValue("@StartSequence", e.StartSequence);
        cmd.Parameters.AddWithValue("@EndSequence", e.EndSequence);
        cmd.Parameters.AddWithValue("@Summary", e.Summary);
        cmd.Parameters.AddWithValue("@OriginalMessageCount", e.OriginalMessageCount);
        cmd.Parameters.AddWithValue("@ModelName", e.ModelName);
        cmd.Parameters.AddWithValue("@CreatedAt", e.CreatedAt);
        cmd.Parameters.AddWithValue("@UpdatedAt", e.UpdatedAt);
    }
}
