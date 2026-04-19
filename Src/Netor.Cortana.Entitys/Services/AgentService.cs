using Microsoft.Data.Sqlite;

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Netor.Cortana.Entitys.Services
{
    /// <summary>
    /// 智能体数据服务，提供对 Agents 表的增删改查操作。
    /// </summary>
    public sealed class AgentService
    {
        private readonly CortanaDbContext _db;

        /// <summary>
        /// 初始化智能体服务。
        /// </summary>
        /// <param name="db">数据库上下文</param>
        public AgentService(CortanaDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// 获取所有已启用的智能体，按排序权重升序排列。
        /// </summary>
        public List<AgentEntity> GetAll()
        {
            return _db.Query(
                "SELECT * FROM Agents WHERE IsEnabled = 1 ORDER BY SortOrder, CreatedTimestamp DESC",
                ReadEntity);
        }

        /// <summary>
        /// 根据 ID 获取单个智能体。
        /// </summary>
        /// <param name="id">智能体 ID</param>
        /// <returns>智能体实体，不存在时返回 null</returns>
        public AgentEntity? GetById(string id)
        {
            return _db.QueryFirstOrDefault(
                "SELECT * FROM Agents WHERE Id = @Id",
                ReadEntity,
                cmd => cmd.Parameters.AddWithValue("@Id", id));
        }

        /// <summary>
        /// 根据名称精确查找已启用的智能体。
        /// </summary>
        /// <param name="name">智能体名称</param>
        /// <returns>智能体实体，不存在时返回 null</returns>
        public AgentEntity? GetByName(string name)
        {
            return _db.QueryFirstOrDefault(
                "SELECT * FROM Agents WHERE Name = @Name AND IsEnabled = 1",
                ReadEntity,
                cmd => cmd.Parameters.AddWithValue("@Name", name));
        }

        /// <summary>
        /// 添加一个新的智能体。
        /// </summary>
        /// <param name="entity">智能体实体</param>
        public void Add(AgentEntity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);

            entity.CreatedTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            entity.UpdatedTimestamp = entity.CreatedTimestamp;

            _db.Execute(InsertSql, cmd => BindEntity(cmd, entity));
        }

        /// <summary>
        /// 更新已有的智能体。
        /// </summary>
        /// <param name="entity">智能体实体</param>
        public void Update(AgentEntity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);

            entity.UpdatedTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            _db.Execute("""
                UPDATE Agents SET
                    UpdatedTimestamp = @UpdatedTimestamp, Name = @Name, Instructions = @Instructions,
                    Description = @Description, Image = @Image,
                    Avatar = @Avatar, DefaultProviderId = @DefaultProviderId, DefaultModelId = @DefaultModelId,
                    Temperature = @Temperature, MaxTokens = @MaxTokens, TopP = @TopP,
                    FrequencyPenalty = @FrequencyPenalty, PresencePenalty = @PresencePenalty,
                    MaxHistoryMessages = @MaxHistoryMessages,
                    IsDefault = @IsDefault, IsEnabled = @IsEnabled, SortOrder = @SortOrder,
                    EnabledPluginIds = @EnabledPluginIds, EnabledMcpServerIds = @EnabledMcpServerIds
                WHERE Id = @Id
                """,
                cmd => BindEntity(cmd, entity));
        }

        /// <summary>
        /// 根据 ID 删除智能体。
        /// </summary>
        /// <param name="id">智能体 ID</param>
        public void Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id cannot be null or empty.", nameof(id));

            _db.Execute("DELETE FROM Agents WHERE Id = @Id",
                cmd => cmd.Parameters.AddWithValue("@Id", id));
        }

        /// <summary>
        /// 将指定智能体设为默认，同时清除其他智能体的默认标记。
        /// </summary>
        /// <param name="id">要设为默认的智能体 ID</param>
        public void SetDefault(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Id cannot be null or empty.", nameof(id));

            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            _db.Execute("UPDATE Agents SET IsDefault = 0, UpdatedTimestamp = @Now WHERE IsDefault = 1",
                cmd => cmd.Parameters.AddWithValue("@Now", now));

            _db.Execute("UPDATE Agents SET IsDefault = 1, UpdatedTimestamp = @Now WHERE Id = @Id",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@Now", now);
                    cmd.Parameters.AddWithValue("@Id", id);
                });
        }

        private const string InsertSql = """
            INSERT INTO Agents (Id, CreatedTimestamp, UpdatedTimestamp, Name, Instructions, Description, Image,
                Avatar, DefaultProviderId, DefaultModelId,
                Temperature, MaxTokens, TopP, FrequencyPenalty, PresencePenalty, MaxHistoryMessages,
                IsDefault, IsEnabled, SortOrder, EnabledPluginIds, EnabledMcpServerIds)
            VALUES (@Id, @CreatedTimestamp, @UpdatedTimestamp, @Name, @Instructions, @Description, @Image,
                @Avatar, @DefaultProviderId, @DefaultModelId,
                @Temperature, @MaxTokens, @TopP, @FrequencyPenalty, @PresencePenalty, @MaxHistoryMessages,
                @IsDefault, @IsEnabled, @SortOrder, @EnabledPluginIds, @EnabledMcpServerIds)
            """;

        private static AgentEntity ReadEntity(SqliteDataReader r) => new()
        {
            Id = r.GetString(r.GetOrdinal("Id")),
            CreatedTimestamp = r.GetInt64(r.GetOrdinal("CreatedTimestamp")),
            UpdatedTimestamp = r.GetInt64(r.GetOrdinal("UpdatedTimestamp")),
            Name = r.GetString(r.GetOrdinal("Name")),
            Instructions = r.GetString(r.GetOrdinal("Instructions")),
            Description = r.GetString(r.GetOrdinal("Description")),
            Image = r.GetString(r.GetOrdinal("Image")),
            Avatar = r.GetString(r.GetOrdinal("Avatar")),
            DefaultProviderId = r.GetString(r.GetOrdinal("DefaultProviderId")),
            DefaultModelId = r.GetString(r.GetOrdinal("DefaultModelId")),
            Temperature = r.GetDouble(r.GetOrdinal("Temperature")),
            MaxTokens = r.GetInt32(r.GetOrdinal("MaxTokens")),
            TopP = r.GetDouble(r.GetOrdinal("TopP")),
            FrequencyPenalty = r.GetDouble(r.GetOrdinal("FrequencyPenalty")),
            PresencePenalty = r.GetDouble(r.GetOrdinal("PresencePenalty")),
            MaxHistoryMessages = r.GetInt32(r.GetOrdinal("MaxHistoryMessages")),
            IsDefault = r.GetBoolean(r.GetOrdinal("IsDefault")),
            IsEnabled = r.GetBoolean(r.GetOrdinal("IsEnabled")),
            SortOrder = r.GetInt32(r.GetOrdinal("SortOrder")),
            EnabledPluginIds = JsonSerializer.Deserialize(r.GetString(r.GetOrdinal("EnabledPluginIds")), EntityJsonContext.Default.ListString) ?? [],
            EnabledMcpServerIds = JsonSerializer.Deserialize(r.GetString(r.GetOrdinal("EnabledMcpServerIds")), EntityJsonContext.Default.ListString) ?? []
        };

        private static void BindEntity(SqliteCommand cmd, AgentEntity e)
        {
            cmd.Parameters.AddWithValue("@Id", e.Id);
            cmd.Parameters.AddWithValue("@CreatedTimestamp", e.CreatedTimestamp);
            cmd.Parameters.AddWithValue("@UpdatedTimestamp", e.UpdatedTimestamp);
            cmd.Parameters.AddWithValue("@Name", e.Name);
            cmd.Parameters.AddWithValue("@Instructions", e.Instructions);
            cmd.Parameters.AddWithValue("@Description", e.Description);
            cmd.Parameters.AddWithValue("@Image", e.Image);
            cmd.Parameters.AddWithValue("@Avatar", e.Avatar);
            cmd.Parameters.AddWithValue("@DefaultProviderId", e.DefaultProviderId);
            cmd.Parameters.AddWithValue("@DefaultModelId", e.DefaultModelId);
            cmd.Parameters.AddWithValue("@Temperature", e.Temperature);
            cmd.Parameters.AddWithValue("@MaxTokens", e.MaxTokens);
            cmd.Parameters.AddWithValue("@TopP", e.TopP);
            cmd.Parameters.AddWithValue("@FrequencyPenalty", e.FrequencyPenalty);
            cmd.Parameters.AddWithValue("@PresencePenalty", e.PresencePenalty);
            cmd.Parameters.AddWithValue("@MaxHistoryMessages", e.MaxHistoryMessages);
            cmd.Parameters.AddWithValue("@IsDefault", e.IsDefault);
            cmd.Parameters.AddWithValue("@IsEnabled", e.IsEnabled);
            cmd.Parameters.AddWithValue("@SortOrder", e.SortOrder);
            cmd.Parameters.AddWithValue("@EnabledPluginIds", JsonSerializer.Serialize(e.EnabledPluginIds, EntityJsonContext.Default.ListString));
            cmd.Parameters.AddWithValue("@EnabledMcpServerIds", JsonSerializer.Serialize(e.EnabledMcpServerIds, EntityJsonContext.Default.ListString));
        }
    }
}
