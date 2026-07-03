using Microsoft.Data.Sqlite;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 会议模式消息数据服务（MeetingMessages 表）。
/// </summary>
public sealed class MeetingMessageService
{
    private readonly CortanaDbContext _db;

    public MeetingMessageService(CortanaDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>追加消息，事务内分配会议内序号。</summary>
    public string Append(MeetingMessageEntity message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(message.Id))
        {
            message.Id = Guid.NewGuid().ToString("N");
        }

        message.CreatedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        _db.ExecuteInTransaction((conn, transaction) =>
        {
            using var sequenceCommand = conn.CreateCommand();
            sequenceCommand.Transaction = transaction;
            sequenceCommand.CommandText = "SELECT IFNULL(MAX(Sequence), -1) + 1 FROM MeetingMessages WHERE MeetingId = @MeetingId";
            sequenceCommand.Parameters.AddWithValue("@MeetingId", message.MeetingId);
            message.Sequence = Convert.ToInt32(sequenceCommand.ExecuteScalar());

            using var insertCommand = conn.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = InsertSql;
            BindEntity(insertCommand, message);
            insertCommand.ExecuteNonQuery();

            return message.Id;
        });

        return message.Id;
    }

    /// <summary>保存或更新流式草稿，保证进行中的会议也可以从历史记录恢复。</summary>
    public string UpsertPartialDraft(MeetingMessageEntity message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(message.Id))
        {
            message.Id = Guid.NewGuid().ToString("N");
        }

        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        message.CreatedAt = now;
        message.IsPartial = true;

        _db.ExecuteInTransaction((conn, transaction) =>
        {
            using var existsCommand = conn.CreateCommand();
            existsCommand.Transaction = transaction;
            existsCommand.CommandText = "SELECT COUNT(*) FROM MeetingMessages WHERE Id = @Id";
            existsCommand.Parameters.AddWithValue("@Id", message.Id);
            var exists = Convert.ToInt32(existsCommand.ExecuteScalar()) > 0;

            if (!exists)
            {
                using var sequenceCommand = conn.CreateCommand();
                sequenceCommand.Transaction = transaction;
                sequenceCommand.CommandText = "SELECT IFNULL(MAX(Sequence), -1) + 1 FROM MeetingMessages WHERE MeetingId = @MeetingId";
                sequenceCommand.Parameters.AddWithValue("@MeetingId", message.MeetingId);
                message.Sequence = Convert.ToInt32(sequenceCommand.ExecuteScalar());

                using var insertCommand = conn.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = InsertSql;
                BindEntity(insertCommand, message);
                insertCommand.ExecuteNonQuery();
                return message.Id;
            }

            using var updateCommand = conn.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = UpdateSql;
            BindEntity(updateCommand, message);
            updateCommand.ExecuteNonQuery();
            return message.Id;
        });

        return message.Id;
    }

    /// <summary>将流式草稿转为正式消息；如果草稿不存在则按正式消息追加。</summary>
    public string CompleteDraft(MeetingMessageEntity message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var existing = GetById(message.Id);
        if (existing is null)
        {
            return Append(message);
        }

        message.Sequence = existing.Sequence;
        message.CreatedAt = existing.CreatedAt;
        message.IsPartial = false;
        message.ErrorMessage = null;
        _db.Execute(UpdateSql, cmd => BindEntity(cmd, message));
        return message.Id;
    }

    /// <summary>追加主持人总结消息。</summary>
    public MeetingMessageEntity AppendSummary(string meetingId, string contentMd)
    {
        var message = new MeetingMessageEntity
        {
            MeetingId = meetingId,
            SpeakerKind = "host",
            SpeakerId = "system-meeting-host",
            SpeakerName = "主持人",
            ContentMd = contentMd,
            MessageRole = "summary",
        };

        Append(message);
        return message;
    }

    /// <summary>异步追加主持人总结消息。</summary>
    public Task<MeetingMessageEntity> AppendSummaryAsync(
        string meetingId,
        string contentMd,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(AppendSummary(meetingId, contentMd));
    }

    /// <summary>追加流式异常时保存的部分消息。</summary>
    public MeetingMessageEntity AppendPartial(
        string meetingId,
        string speakerKind,
        string speakerId,
        string speakerName,
        string partialContent,
        string errorMessage)
    {
        var message = new MeetingMessageEntity
        {
            MeetingId = meetingId,
            SpeakerKind = speakerKind,
            SpeakerId = speakerId,
            SpeakerName = speakerName,
            ContentMd = partialContent,
            MessageRole = "normal",
            IsPartial = true,
            ErrorMessage = errorMessage,
        };

        Append(message);
        return message;
    }

    /// <summary>异步追加流式异常时保存的部分消息。</summary>
    public Task<MeetingMessageEntity> AppendPartialAsync(
        string meetingId,
        string speakerKind,
        string speakerId,
        string speakerName,
        string partialContent,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(AppendPartial(
            meetingId,
            speakerKind,
            speakerId,
            speakerName,
            partialContent,
            errorMessage));
    }

    /// <summary>查询会议全部消息，含部分消息。</summary>
    public List<MeetingMessageEntity> ListByMeeting(string meetingId)
    {
        return _db.Query("""
            SELECT * FROM MeetingMessages
            WHERE MeetingId = @MeetingId
            ORDER BY Sequence ASC
            """,
            ReadEntity,
            cmd => cmd.Parameters.AddWithValue("@MeetingId", meetingId));
    }

    /// <summary>查询指定序号之后的完整消息。</summary>
    public List<MeetingMessageEntity> ListCompleteBySequence(string meetingId, int fromSequence)
    {
        return _db.Query("""
            SELECT * FROM MeetingMessages
            WHERE MeetingId = @MeetingId AND Sequence >= @FromSequence AND IsPartial = 0
            ORDER BY Sequence ASC
            """,
            ReadEntity,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@MeetingId", meetingId);
                cmd.Parameters.AddWithValue("@FromSequence", fromSequence);
            });
    }

    /// <summary>在指定事务内查询指定序号之后的完整消息。</summary>
    public List<MeetingMessageEntity> ListCompleteBySequence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string meetingId,
        int fromSequence)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT * FROM MeetingMessages
            WHERE MeetingId = @MeetingId AND Sequence >= @FromSequence AND IsPartial = 0
            ORDER BY Sequence ASC
            """;
        command.Parameters.AddWithValue("@MeetingId", meetingId);
        command.Parameters.AddWithValue("@FromSequence", fromSequence);

        using var reader = command.ExecuteReader();
        var result = new List<MeetingMessageEntity>();
        while (reader.Read())
        {
            result.Add(ReadEntity(reader));
        }

        return result;
    }

    /// <summary>查询会议全部完整消息。</summary>
    public List<MeetingMessageEntity> ListCompleteByMeeting(string meetingId)
    {
        return _db.Query("""
            SELECT * FROM MeetingMessages
            WHERE MeetingId = @MeetingId AND IsPartial = 0
            ORDER BY Sequence ASC
            """,
            ReadEntity,
            cmd => cmd.Parameters.AddWithValue("@MeetingId", meetingId));
    }

    /// <summary>在指定事务内查询会议全部完整消息。</summary>
    public List<MeetingMessageEntity> ListCompleteByMeeting(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string meetingId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT * FROM MeetingMessages
            WHERE MeetingId = @MeetingId AND IsPartial = 0
            ORDER BY Sequence ASC
            """;
        command.Parameters.AddWithValue("@MeetingId", meetingId);

        using var reader = command.ExecuteReader();
        var result = new List<MeetingMessageEntity>();
        while (reader.Read())
        {
            result.Add(ReadEntity(reader));
        }

        return result;
    }

    /// <summary>查询序号范围内的消息。</summary>
    public List<MeetingMessageEntity> ListBySequenceRange(string meetingId, int fromSequence, int toSequence)
    {
        return _db.Query("""
            SELECT * FROM MeetingMessages
            WHERE MeetingId = @MeetingId AND Sequence >= @FromSequence AND Sequence <= @ToSequence
            ORDER BY Sequence ASC
            """,
            ReadEntity,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@MeetingId", meetingId);
                cmd.Parameters.AddWithValue("@FromSequence", fromSequence);
                cmd.Parameters.AddWithValue("@ToSequence", toSequence);
            });
    }

    /// <summary>查询指定序号之后的全部消息，含部分消息。</summary>
    public List<MeetingMessageEntity> ListBySequence(string meetingId, int fromSequence)
    {
        return _db.Query("""
            SELECT * FROM MeetingMessages
            WHERE MeetingId = @MeetingId AND Sequence >= @FromSequence
            ORDER BY Sequence ASC
            """,
            ReadEntity,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@MeetingId", meetingId);
                cmd.Parameters.AddWithValue("@FromSequence", fromSequence);
            });
    }

    /// <summary>获取最近一条 summary 消息。</summary>
    public MeetingMessageEntity? GetLatestSummary(string meetingId)
    {
        return _db.QueryFirstOrDefault("""
            SELECT * FROM MeetingMessages
            WHERE MeetingId = @MeetingId AND MessageRole = 'summary'
            ORDER BY Sequence DESC LIMIT 1
            """,
            ReadEntity,
            cmd => cmd.Parameters.AddWithValue("@MeetingId", meetingId));
    }

    /// <summary>根据消息 ID 获取会议消息。</summary>
    public MeetingMessageEntity? GetById(string id)
    {
        return _db.QueryFirstOrDefault(
            "SELECT * FROM MeetingMessages WHERE Id = @Id",
            ReadEntity,
            cmd => cmd.Parameters.AddWithValue("@Id", id));
    }

    /// <summary>删除指定消息。</summary>
    public void DeleteById(string id)
    {
        _db.Execute(
            "DELETE FROM MeetingMessages WHERE Id = @Id",
            cmd => cmd.Parameters.AddWithValue("@Id", id));
    }

    /// <summary>异步获取最近一条 summary 消息。</summary>
    public Task<MeetingMessageEntity?> GetLatestSummaryAsync(
        string meetingId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetLatestSummary(meetingId));
    }

    /// <summary>统计会议消息数量。</summary>
    public int CountByMeeting(string meetingId)
    {
        return _db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM MeetingMessages WHERE MeetingId = @MeetingId",
            cmd => cmd.Parameters.AddWithValue("@MeetingId", meetingId));
    }

    /// <summary>获取下一条消息序号。</summary>
    public int GetNextSequence(string meetingId)
    {
        return _db.ExecuteScalar<int>(
            "SELECT IFNULL(MAX(Sequence), -1) + 1 FROM MeetingMessages WHERE MeetingId = @MeetingId",
            cmd => cmd.Parameters.AddWithValue("@MeetingId", meetingId));
    }

    private const string InsertSql = """
        INSERT INTO MeetingMessages (
            Id, MeetingId, Sequence, SpeakerKind, SpeakerId, SpeakerName, ContentMd,
            ThinkingMd, ToolCallsJson, AttachmentsJson, MessageRole, AwaitingUserReply,
            IsPartial, ErrorMessage, CreatedAt
        ) VALUES (
            @Id, @MeetingId, @Sequence, @SpeakerKind, @SpeakerId, @SpeakerName, @ContentMd,
            @ThinkingMd, @ToolCallsJson, @AttachmentsJson, @MessageRole, @AwaitingUserReply,
            @IsPartial, @ErrorMessage, @CreatedAt
        )
        """;

    private const string UpdateSql = """
        UPDATE MeetingMessages
        SET SpeakerKind = @SpeakerKind,
            SpeakerId = @SpeakerId,
            SpeakerName = @SpeakerName,
            ContentMd = @ContentMd,
            ThinkingMd = @ThinkingMd,
            ToolCallsJson = @ToolCallsJson,
            AttachmentsJson = @AttachmentsJson,
            MessageRole = @MessageRole,
            AwaitingUserReply = @AwaitingUserReply,
            IsPartial = @IsPartial,
            ErrorMessage = @ErrorMessage
        WHERE Id = @Id
        """;

    private static MeetingMessageEntity ReadEntity(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("Id")),
        MeetingId = r.GetString(r.GetOrdinal("MeetingId")),
        Sequence = r.GetInt32(r.GetOrdinal("Sequence")),
        SpeakerKind = r.GetString(r.GetOrdinal("SpeakerKind")),
        SpeakerId = r.GetString(r.GetOrdinal("SpeakerId")),
        SpeakerName = r.GetString(r.GetOrdinal("SpeakerName")),
        ContentMd = r.GetString(r.GetOrdinal("ContentMd")),
        ThinkingMd = ReadNullableString(r, "ThinkingMd"),
        ToolCallsJson = ReadNullableString(r, "ToolCallsJson"),
        AttachmentsJson = ReadNullableString(r, "AttachmentsJson"),
        MessageRole = r.GetString(r.GetOrdinal("MessageRole")),
        AwaitingUserReply = r.GetInt32(r.GetOrdinal("AwaitingUserReply")) != 0,
        IsPartial = r.GetInt32(r.GetOrdinal("IsPartial")) != 0,
        ErrorMessage = ReadNullableString(r, "ErrorMessage"),
        CreatedAt = r.GetInt64(r.GetOrdinal("CreatedAt")),
    };

    private static void BindEntity(SqliteCommand cmd, MeetingMessageEntity e)
    {
        cmd.Parameters.AddWithValue("@Id", e.Id);
        cmd.Parameters.AddWithValue("@MeetingId", e.MeetingId);
        cmd.Parameters.AddWithValue("@Sequence", e.Sequence);
        cmd.Parameters.AddWithValue("@SpeakerKind", e.SpeakerKind);
        cmd.Parameters.AddWithValue("@SpeakerId", e.SpeakerId);
        cmd.Parameters.AddWithValue("@SpeakerName", e.SpeakerName);
        cmd.Parameters.AddWithValue("@ContentMd", e.ContentMd);
        cmd.Parameters.AddWithValue("@ThinkingMd", (object?)e.ThinkingMd ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ToolCallsJson", (object?)e.ToolCallsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@AttachmentsJson", (object?)e.AttachmentsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@MessageRole", e.MessageRole);
        cmd.Parameters.AddWithValue("@AwaitingUserReply", e.AwaitingUserReply ? 1 : 0);
        cmd.Parameters.AddWithValue("@IsPartial", e.IsPartial ? 1 : 0);
        cmd.Parameters.AddWithValue("@ErrorMessage", (object?)e.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@CreatedAt", e.CreatedAt);
    }

    private static string? ReadNullableString(SqliteDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord) ? null : r.GetString(ord);
    }
}
