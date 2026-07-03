using Microsoft.Data.Sqlite;

using System;
using System.Collections.Generic;

namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// 压缩段落数据服务，提供对 CompactionSegments 表的读写操作。
/// </summary>
public sealed class CompactionSegmentService(CortanaDbContext db)
{
    private readonly CortanaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>
    /// 获取指定会话的所有段落，按 SegmentIndex 正序排列。
    /// </summary>
    public List<CompactionSegmentEntity> GetBySessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return [];

        return _db.Query(
            "SELECT * FROM CompactionSegments WHERE SessionId = @SessionId ORDER BY SegmentIndex ASC",
            ReadEntity,
            cmd => cmd.Parameters.AddWithValue("@SessionId", sessionId));
    }

    /// <summary>
    /// 获取指定会话的最大段落序号，无段落时返回 -1。
    /// </summary>
    public int GetMaxSegmentIndex(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return -1;

        var result = _db.ExecuteScalar<object>(
            "SELECT MAX(SegmentIndex) FROM CompactionSegments WHERE SessionId = @SessionId",
            cmd => cmd.Parameters.AddWithValue("@SessionId", sessionId));

        return result is long v ? (int)v : -1;
    }

    /// <summary>
    /// 获取指定会话已被段落覆盖的最大消息索引，无段落时返回 -1。
    /// </summary>
    public int GetMaxCoveredMessageIndex(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return -1;

        var result = _db.ExecuteScalar<object>(
            "SELECT MAX(EndMessageIndex) FROM CompactionSegments WHERE SessionId = @SessionId",
            cmd => cmd.Parameters.AddWithValue("@SessionId", sessionId));

        return result is long v ? (int)v : -1;
    }

    /// <summary>
    /// 插入一个新的压缩段落。
    /// </summary>
    public void Add(CompactionSegmentEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        entity.CreatedTimestamp = now;
        entity.UpdatedTimestamp = now;

        _db.Execute(InsertSql, cmd => BindEntity(cmd, entity));
    }

    /// <summary>
    /// 获取指定会话的段落总数。
    /// </summary>
    public int GetSegmentCount(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return 0;

        return (int)_db.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM CompactionSegments WHERE SessionId = @SessionId",
            cmd => cmd.Parameters.AddWithValue("@SessionId", sessionId));
    }

    /// <summary>
    /// 删除指定会话的所有段落（用于会话删除时级联清理）。
    /// </summary>
    public int DeleteBySessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return 0;

        return _db.Execute(
            "DELETE FROM CompactionSegments WHERE SessionId = @SessionId",
            cmd => cmd.Parameters.AddWithValue("@SessionId", sessionId));
    }

    private const string InsertSql = """
        INSERT INTO CompactionSegments
            (Id, CreatedTimestamp, UpdatedTimestamp, SessionId, SegmentIndex,
             StartMessageIndex, EndMessageIndex, Summary, OriginalMessageCount, ModelName)
        VALUES
            (@Id, @CreatedTimestamp, @UpdatedTimestamp, @SessionId, @SegmentIndex,
             @StartMessageIndex, @EndMessageIndex, @Summary, @OriginalMessageCount, @ModelName)
        """;

    private static CompactionSegmentEntity ReadEntity(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("Id")),
        CreatedTimestamp = r.GetInt64(r.GetOrdinal("CreatedTimestamp")),
        UpdatedTimestamp = r.GetInt64(r.GetOrdinal("UpdatedTimestamp")),
        SessionId = r.GetString(r.GetOrdinal("SessionId")),
        SegmentIndex = r.GetInt32(r.GetOrdinal("SegmentIndex")),
        StartMessageIndex = r.GetInt32(r.GetOrdinal("StartMessageIndex")),
        EndMessageIndex = r.GetInt32(r.GetOrdinal("EndMessageIndex")),
        Summary = r.GetString(r.GetOrdinal("Summary")),
        OriginalMessageCount = r.GetInt32(r.GetOrdinal("OriginalMessageCount")),
        ModelName = r.GetString(r.GetOrdinal("ModelName")),
    };

    private static void BindEntity(SqliteCommand cmd, CompactionSegmentEntity e)
    {
        cmd.Parameters.AddWithValue("@Id", e.Id);
        cmd.Parameters.AddWithValue("@CreatedTimestamp", e.CreatedTimestamp);
        cmd.Parameters.AddWithValue("@UpdatedTimestamp", e.UpdatedTimestamp);
        cmd.Parameters.AddWithValue("@SessionId", e.SessionId);
        cmd.Parameters.AddWithValue("@SegmentIndex", e.SegmentIndex);
        cmd.Parameters.AddWithValue("@StartMessageIndex", e.StartMessageIndex);
        cmd.Parameters.AddWithValue("@EndMessageIndex", e.EndMessageIndex);
        cmd.Parameters.AddWithValue("@Summary", e.Summary);
        cmd.Parameters.AddWithValue("@OriginalMessageCount", e.OriginalMessageCount);
        cmd.Parameters.AddWithValue("@ModelName", e.ModelName);
    }
}
