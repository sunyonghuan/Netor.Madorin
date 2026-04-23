using Microsoft.Data.Sqlite;

using System;
using System.Collections.Generic;

namespace Netor.Cortana.Entitys.Services
{
    /// <summary>
    /// 聊天消息数据服务，提供对 ChatMessages 表的查询操作。
    /// </summary>
    public sealed class ChatMessageService
    {
        private readonly CortanaDbContext _db;

        /// <summary>
        /// 初始化聊天消息服务。
        /// </summary>
        /// <param name="db">数据库上下文</param>
        public ChatMessageService(CortanaDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// 获取指定会话下的所有消息，按创建时间正序排列。
        /// </summary>
        /// <param name="sessionId">会话 ID</param>
        /// <returns>消息列表</returns>
        public List<ChatMessageEntity> GetBySessionId(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return [];

            return _db.Query(
                "SELECT * FROM ChatMessages WHERE SessionId = @SessionId ORDER BY CreatedTimestamp",
                ReadEntity,
                cmd => cmd.Parameters.AddWithValue("@SessionId", sessionId));
        }

        public static ChatMessageEntity ReadEntity(SqliteDataReader r)
        {
            var createdAtOrdinal = r.GetOrdinal("CreatedAt");

            // ContentsJson 列在老数据库中可能不存在（迁移前），做兼容读取
            string contentsJson = string.Empty;
            try
            {
                var idx = r.GetOrdinal("ContentsJson");
                if (!r.IsDBNull(idx)) contentsJson = r.GetString(idx);
            }
            catch (IndexOutOfRangeException)
            {
                // 未迁移的老库，忽略
            }

            return new ChatMessageEntity
            {
                Id = r.GetString(r.GetOrdinal("Id")),
                CreatedTimestamp = r.GetInt64(r.GetOrdinal("CreatedTimestamp")),
                UpdatedTimestamp = r.GetInt64(r.GetOrdinal("UpdatedTimestamp")),
                SessionId = r.GetString(r.GetOrdinal("SessionId")),
                Role = r.GetString(r.GetOrdinal("Role")),
                AuthorName = r.GetString(r.GetOrdinal("AuthorName")),
                Content = r.GetString(r.GetOrdinal("Content")),
                ContentsJson = contentsJson,
                TokenCount = r.GetInt32(r.GetOrdinal("TokenCount")),
                ModelName = r.GetString(r.GetOrdinal("ModelName")),
                CreatedAt = r.IsDBNull(createdAtOrdinal) ? null : DateTimeOffset.Parse(r.GetString(createdAtOrdinal))
            };
        }

        public static void BindEntity(SqliteCommand cmd, ChatMessageEntity e)
        {
            cmd.Parameters.AddWithValue("@Id", e.Id);
            cmd.Parameters.AddWithValue("@CreatedTimestamp", e.CreatedTimestamp);
            cmd.Parameters.AddWithValue("@UpdatedTimestamp", e.UpdatedTimestamp);
            cmd.Parameters.AddWithValue("@SessionId", e.SessionId);
            cmd.Parameters.AddWithValue("@Role", e.Role);
            cmd.Parameters.AddWithValue("@AuthorName", e.AuthorName);
            cmd.Parameters.AddWithValue("@Content", e.Content);
            cmd.Parameters.AddWithValue("@ContentsJson", e.ContentsJson ?? string.Empty);
            cmd.Parameters.AddWithValue("@TokenCount", e.TokenCount);
            cmd.Parameters.AddWithValue("@ModelName", e.ModelName);
            cmd.Parameters.AddWithValue("@CreatedAt", (object?)e.CreatedAt?.ToString("O") ?? DBNull.Value);
        }
    }
}
