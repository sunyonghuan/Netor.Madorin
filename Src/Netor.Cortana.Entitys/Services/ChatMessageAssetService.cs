using Microsoft.Data.Sqlite;

using System;
using System.Collections.Generic;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 聊天消息资源数据服务，提供对 ChatMessageAssets 表的增删改查操作。
/// </summary>
/// <param name="db">数据库上下文</param>
public sealed class ChatMessageAssetService(CortanaDbContext db)
{
    private readonly CortanaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>
    /// 获取指定会话下的所有资源，按创建时间和排序序号排列。
    /// </summary>
    public List<ChatMessageAssetEntity> GetBySessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return [];

        return _db.Query(
            "SELECT * FROM ChatMessageAssets WHERE SessionId = @SessionId AND Status = 'active' ORDER BY CreatedTimestamp, SortOrder",
            ReadEntity,
            cmd => cmd.Parameters.AddWithValue("@SessionId", sessionId));
    }

    /// <summary>
    /// 获取指定消息下的所有资源。
    /// </summary>
    public List<ChatMessageAssetEntity> GetByMessageId(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return [];

        return _db.Query(
            "SELECT * FROM ChatMessageAssets WHERE MessageId = @MessageId AND Status = 'active' ORDER BY SortOrder",
            ReadEntity,
            cmd => cmd.Parameters.AddWithValue("@MessageId", messageId));
    }

    /// <summary>
    /// 插入单条资源记录。
    /// </summary>
    public void Add(ChatMessageAssetEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        entity.CreatedTimestamp = now;
        entity.UpdatedTimestamp = now;

        _db.Execute(InsertSql, cmd => BindEntity(cmd, entity));
    }

    /// <summary>
    /// 批量插入资源记录。
    /// </summary>
    public void BatchInsert(IEnumerable<ChatMessageAssetEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        _db.ExecuteInTransaction(conn =>
        {
            foreach (var e in entities)
            {
                e.CreatedTimestamp = now;
                e.UpdatedTimestamp = now;

                using var cmd = conn.CreateCommand();
                cmd.CommandText = InsertSql;
                BindEntity(cmd, e);
                cmd.ExecuteNonQuery();
            }
        });
    }

    /// <summary>
    /// 软删除指定资源（标记为 deleted）。
    /// </summary>
    public void SoftDelete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;

        _db.Execute(
            "UPDATE ChatMessageAssets SET Status = 'deleted', UpdatedTimestamp = @Now WHERE Id = @Id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Now", DateTimeOffset.Now.ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("@Id", id);
            });
    }

    /// <summary>
    /// 删除指定会话下的所有资源记录。
    /// </summary>
    public int DeleteBySessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return 0;

        return _db.Execute(
            "DELETE FROM ChatMessageAssets WHERE SessionId = @SessionId",
            cmd => cmd.Parameters.AddWithValue("@SessionId", sessionId));
    }

    private const string InsertSql = """
        INSERT INTO ChatMessageAssets
            (Id, CreatedTimestamp, UpdatedTimestamp, SessionId, MessageId, Role, AssetGroup, AssetKind, MimeType,
             OriginalName, RelativePath, FileSizeBytes, Sha256, SortOrder, Width, Height, DurationMs, SourceType, Status)
        VALUES
            (@Id, @CreatedTimestamp, @UpdatedTimestamp, @SessionId, @MessageId, @Role, @AssetGroup, @AssetKind, @MimeType,
             @OriginalName, @RelativePath, @FileSizeBytes, @Sha256, @SortOrder, @Width, @Height, @DurationMs, @SourceType, @Status)
        """;

    public static ChatMessageAssetEntity ReadEntity(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("Id")),
        CreatedTimestamp = r.GetInt64(r.GetOrdinal("CreatedTimestamp")),
        UpdatedTimestamp = r.GetInt64(r.GetOrdinal("UpdatedTimestamp")),
        SessionId = r.GetString(r.GetOrdinal("SessionId")),
        MessageId = r.GetString(r.GetOrdinal("MessageId")),
        Role = r.GetString(r.GetOrdinal("Role")),
        AssetGroup = r.GetString(r.GetOrdinal("AssetGroup")),
        AssetKind = r.GetString(r.GetOrdinal("AssetKind")),
        MimeType = r.GetString(r.GetOrdinal("MimeType")),
        OriginalName = r.GetString(r.GetOrdinal("OriginalName")),
        RelativePath = r.GetString(r.GetOrdinal("RelativePath")),
        FileSizeBytes = r.GetInt64(r.GetOrdinal("FileSizeBytes")),
        Sha256 = r.GetString(r.GetOrdinal("Sha256")),
        SortOrder = r.GetInt32(r.GetOrdinal("SortOrder")),
        Width = r.GetInt32(r.GetOrdinal("Width")),
        Height = r.GetInt32(r.GetOrdinal("Height")),
        DurationMs = r.GetInt64(r.GetOrdinal("DurationMs")),
        SourceType = r.GetString(r.GetOrdinal("SourceType")),
        Status = r.GetString(r.GetOrdinal("Status")),
    };

    public static void BindEntity(SqliteCommand cmd, ChatMessageAssetEntity e)
    {
        cmd.Parameters.AddWithValue("@Id", e.Id);
        cmd.Parameters.AddWithValue("@CreatedTimestamp", e.CreatedTimestamp);
        cmd.Parameters.AddWithValue("@UpdatedTimestamp", e.UpdatedTimestamp);
        cmd.Parameters.AddWithValue("@SessionId", e.SessionId);
        cmd.Parameters.AddWithValue("@MessageId", e.MessageId);
        cmd.Parameters.AddWithValue("@Role", e.Role);
        cmd.Parameters.AddWithValue("@AssetGroup", e.AssetGroup);
        cmd.Parameters.AddWithValue("@AssetKind", e.AssetKind);
        cmd.Parameters.AddWithValue("@MimeType", e.MimeType);
        cmd.Parameters.AddWithValue("@OriginalName", e.OriginalName);
        cmd.Parameters.AddWithValue("@RelativePath", e.RelativePath);
        cmd.Parameters.AddWithValue("@FileSizeBytes", e.FileSizeBytes);
        cmd.Parameters.AddWithValue("@Sha256", e.Sha256);
        cmd.Parameters.AddWithValue("@SortOrder", e.SortOrder);
        cmd.Parameters.AddWithValue("@Width", e.Width);
        cmd.Parameters.AddWithValue("@Height", e.Height);
        cmd.Parameters.AddWithValue("@DurationMs", e.DurationMs);
        cmd.Parameters.AddWithValue("@SourceType", e.SourceType);
        cmd.Parameters.AddWithValue("@Status", e.Status);
    }
}
