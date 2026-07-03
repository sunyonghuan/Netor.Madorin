using Microsoft.Data.Sqlite;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Netor.Cortana.Entitys.Services
{
    /// <summary>
    /// 聊天消息数据服务，提供对 ChatMessages 表的查询操作。
    /// </summary>
    public sealed partial class ChatMessageService
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

            // 前端聊天窗口不显示：
            // 1) 工具消息（role = 'tool'）
            // 2) 任何包含工具调用的助手占位消息。工具调用链条只服务于 AI 协议，不属于用户可读聊天内容。
            return _db.Query(
                "SELECT * FROM ChatMessages\n"
                + "WHERE SessionId = @SessionId\n"
                + "  AND Role <> 'tool'\n"
                + "  AND Content NOT LIKE '[工具调用]%'\n"
                + "  AND NOT (Role = 'assistant'\n"
                + "           AND IFNULL(ContentsJson,'') <> ''\n"
                + "           AND (ContentsJson LIKE '%\"functionCall\"%' OR ContentsJson LIKE '%\"toolCall\"%'))\n"
                // 加入 rowid 作为 tie-breaker，确保同一时间戳的多条消息按入库顺序返回，
                // 避免工具调用链条因时间戳精度不足而错乱。
                + "ORDER BY CreatedTimestamp ASC, rowid ASC",
                ReadDisplayEntity,
                cmd => cmd.Parameters.AddWithValue("@SessionId", sessionId));
        }

        private static ChatMessageEntity ReadDisplayEntity(SqliteDataReader r)
        {
            var entity = ReadEntity(r);
            entity.Content = StripToolBlocks(entity.Content);
            return entity;
        }

        private static string StripToolBlocks(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return string.Empty;

            var cleaned = ToolCallBlockRegex().Replace(content, string.Empty);
            cleaned = ToolResultBlockRegex().Replace(cleaned, string.Empty);
            return cleaned.Trim();
        }

        [GeneratedRegex(@"(?ms)^\[工具调用\]\s*.*?(?=^\[工具调用\]|^\[工具结果\]|\z)")]
        private static partial Regex ToolCallBlockRegex();

        [GeneratedRegex(@"(?ms)^\[工具结果\]\s*.*?(?=^\[工具调用\]|^\[工具结果\]|\z)")]
        private static partial Regex ToolResultBlockRegex();

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

            // AgentId / AgentName 在更老的数据库中可能不存在，迁移前兼容读取。
            string agentId = string.Empty;
            string agentName = string.Empty;
            try
            {
                var idx = r.GetOrdinal("AgentId");
                if (!r.IsDBNull(idx)) agentId = r.GetString(idx);
            }
            catch (IndexOutOfRangeException) { /* 老库未迁移 */ }
            try
            {
                var idx = r.GetOrdinal("AgentName");
                if (!r.IsDBNull(idx)) agentName = r.GetString(idx);
            }
            catch (IndexOutOfRangeException) { /* 老库未迁移 */ }

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
                CreatedAt = r.IsDBNull(createdAtOrdinal) ? null : DateTimeOffset.Parse(r.GetString(createdAtOrdinal)),
                AgentId = agentId,
                AgentName = agentName
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
            cmd.Parameters.AddWithValue("@AgentId", e.AgentId ?? string.Empty);
            cmd.Parameters.AddWithValue("@AgentName", e.AgentName ?? string.Empty);
        }
    }
}
