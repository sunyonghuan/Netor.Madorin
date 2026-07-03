using Microsoft.Data.Sqlite;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 会议模式附件数据服务（MeetingAttachments 表）。
/// </summary>
public sealed class MeetingAttachmentService
{
    private readonly CortanaDbContext _db;

    public MeetingAttachmentService(CortanaDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>新增附件记录。</summary>
    public string Add(MeetingAttachmentEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (string.IsNullOrWhiteSpace(entity.Id))
        {
            entity.Id = Guid.NewGuid().ToString("N");
        }

        if (entity.UploadedAt == 0)
        {
            entity.UploadedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        }

        _db.Execute("""
            INSERT INTO MeetingAttachments (Id, MeetingId, FileName, StoredPath, MimeType, SizeBytes, UploadedAt)
            VALUES (@Id, @MeetingId, @FileName, @StoredPath, @MimeType, @SizeBytes, @UploadedAt)
            """,
            cmd => BindEntity(cmd, entity));
        return entity.Id;
    }

    /// <summary>查询会议附件。</summary>
    public List<MeetingAttachmentEntity> GetByMeetingId(string meetingId)
    {
        return _db.Query("""
            SELECT * FROM MeetingAttachments
            WHERE MeetingId = @MeetingId
            ORDER BY UploadedAt ASC
            """,
            ReadEntity,
            cmd => cmd.Parameters.AddWithValue("@MeetingId", meetingId));
    }

    private static MeetingAttachmentEntity ReadEntity(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("Id")),
        MeetingId = r.GetString(r.GetOrdinal("MeetingId")),
        FileName = r.GetString(r.GetOrdinal("FileName")),
        StoredPath = r.GetString(r.GetOrdinal("StoredPath")),
        MimeType = r.GetString(r.GetOrdinal("MimeType")),
        SizeBytes = r.GetInt64(r.GetOrdinal("SizeBytes")),
        UploadedAt = r.GetInt64(r.GetOrdinal("UploadedAt")),
    };

    private static void BindEntity(SqliteCommand cmd, MeetingAttachmentEntity e)
    {
        cmd.Parameters.AddWithValue("@Id", e.Id);
        cmd.Parameters.AddWithValue("@MeetingId", e.MeetingId);
        cmd.Parameters.AddWithValue("@FileName", e.FileName);
        cmd.Parameters.AddWithValue("@StoredPath", e.StoredPath);
        cmd.Parameters.AddWithValue("@MimeType", e.MimeType);
        cmd.Parameters.AddWithValue("@SizeBytes", e.SizeBytes);
        cmd.Parameters.AddWithValue("@UploadedAt", e.UploadedAt);
    }
}
