using Microsoft.Data.Sqlite;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 会议模式用户插话队列服务（MeetingPendingInputs 表）。
/// </summary>
public sealed class MeetingPendingInputService
{
    private readonly CortanaDbContext _db;

    public MeetingPendingInputService(CortanaDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>追加一条待消费用户输入。</summary>
    public void Enqueue(string meetingId, string content, string? attachmentsJson = null, string kind = "interrupt")
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            INSERT INTO MeetingPendingInputs (MeetingId, Content, AttachmentsJson, Kind, EnqueuedAt, Consumed)
            VALUES (@MeetingId, @Content, @AttachmentsJson, @Kind, @EnqueuedAt, 0)
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@MeetingId", meetingId);
                cmd.Parameters.AddWithValue("@Content", content);
                cmd.Parameters.AddWithValue("@AttachmentsJson", (object?)attachmentsJson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Kind", kind);
                cmd.Parameters.AddWithValue("@EnqueuedAt", now);
            });
    }

    /// <summary>取出所有未消费输入并标为已消费。</summary>
    public List<MeetingPendingInputEntity> Drain(string meetingId)
    {
        return _db.ExecuteInTransaction((conn, transaction) =>
        {
            var rows = new List<MeetingPendingInputEntity>();

            using var queryCommand = conn.CreateCommand();
            queryCommand.Transaction = transaction;
            queryCommand.CommandText = """
                SELECT * FROM MeetingPendingInputs
                WHERE MeetingId = @MeetingId AND Consumed = 0
                ORDER BY EnqueuedAt ASC
                """;
            queryCommand.Parameters.AddWithValue("@MeetingId", meetingId);

            using (var reader = queryCommand.ExecuteReader())
            {
                while (reader.Read())
                {
                    rows.Add(ReadEntity(reader));
                }
            }

            if (rows.Count == 0)
            {
                return rows;
            }

            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            using var updateCommand = conn.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = """
                UPDATE MeetingPendingInputs
                SET Consumed = 1, ConsumedAt = @Now
                WHERE MeetingId = @MeetingId AND Consumed = 0
                """;
            updateCommand.Parameters.AddWithValue("@MeetingId", meetingId);
            updateCommand.Parameters.AddWithValue("@Now", now);
            updateCommand.ExecuteNonQuery();

            return rows;
        });
    }

    /// <summary>查看未消费输入但不消费。</summary>
    public List<MeetingPendingInputEntity> PeekUnconsumed(string meetingId)
    {
        return _db.Query("""
            SELECT * FROM MeetingPendingInputs
            WHERE MeetingId = @MeetingId AND Consumed = 0
            ORDER BY EnqueuedAt ASC
            """,
            ReadEntity,
            cmd => cmd.Parameters.AddWithValue("@MeetingId", meetingId));
    }

    private static MeetingPendingInputEntity ReadEntity(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("Id")),
        MeetingId = r.GetString(r.GetOrdinal("MeetingId")),
        Content = r.GetString(r.GetOrdinal("Content")),
        AttachmentsJson = ReadNullableString(r, "AttachmentsJson"),
        Kind = r.GetString(r.GetOrdinal("Kind")),
        EnqueuedAt = r.GetInt64(r.GetOrdinal("EnqueuedAt")),
        Consumed = r.GetInt32(r.GetOrdinal("Consumed")) != 0,
        ConsumedAt = ReadNullableInt64(r, "ConsumedAt"),
    };

    private static string? ReadNullableString(SqliteDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord) ? null : r.GetString(ord);
    }

    private static long? ReadNullableInt64(SqliteDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord) ? null : r.GetInt64(ord);
    }
}
