using Microsoft.Data.Sqlite;

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Netor.Cortana.Entitys.Services
{
    /// <summary>
    /// MCP 服务器配置数据服务，提供对 McpServers 表的增删改查操作。
    /// </summary>
    public sealed class McpServerService
    {
        private readonly CortanaDbContext _db;

        /// <summary>
        /// 初始化 MCP 服务器配置服务。
        /// </summary>
        /// <param name="db">数据库上下文</param>
        public McpServerService(CortanaDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// 获取所有 MCP 服务器配置，按创建时间降序排列。
        /// </summary>
        public List<McpServerEntity> GetAll()
        {
            return _db.Query(
                "SELECT * FROM McpServers ORDER BY CreatedTimestamp DESC",
                ReadEntity);
        }

        /// <summary>
        /// 获取所有已启用的 MCP 服务器配置。
        /// </summary>
        public List<McpServerEntity> GetEnabled()
        {
            return _db.Query(
                "SELECT * FROM McpServers WHERE IsEnabled = 1 ORDER BY CreatedTimestamp DESC",
                ReadEntity);
        }

        /// <summary>
        /// 根据 ID 获取单个 MCP 服务器配置。
        /// </summary>
        /// <param name="id">MCP 服务器 ID</param>
        /// <returns>实体，不存在时返回 null</returns>
        public McpServerEntity? GetById(string id)
        {
            return _db.QueryFirstOrDefault(
                "SELECT * FROM McpServers WHERE Id = @Id",
                ReadEntity,
                cmd => cmd.Parameters.AddWithValue("@Id", id));
        }

        /// <summary>
        /// 添加一个新的 MCP 服务器配置。
        /// </summary>
        /// <param name="entity">MCP 服务器实体</param>
        public void Add(McpServerEntity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);

            entity.CreatedTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            entity.UpdatedTimestamp = entity.CreatedTimestamp;

            _db.Execute(InsertSql, cmd => BindEntity(cmd, entity));
        }

        /// <summary>
        /// 更新已有的 MCP 服务器配置。
        /// </summary>
        /// <param name="entity">MCP 服务器实体</param>
        public void Update(McpServerEntity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);

            entity.UpdatedTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            _db.Execute("""
                UPDATE McpServers SET
                    UpdatedTimestamp = @UpdatedTimestamp, Name = @Name, TransportType = @TransportType,
                    Command = @Command, Arguments = @Arguments, Url = @Url, ApiKey = @ApiKey,
                    EnvironmentVariables = @EnvironmentVariables, Description = @Description, IsEnabled = @IsEnabled
                WHERE Id = @Id
                """,
                cmd => BindEntity(cmd, entity));
        }

        /// <summary>
        /// 根据 ID 删除 MCP 服务器配置。
        /// </summary>
        /// <param name="id">MCP 服务器 ID</param>
        public void Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id cannot be null or empty.", nameof(id));

            _db.Execute("DELETE FROM McpServers WHERE Id = @Id",
                cmd => cmd.Parameters.AddWithValue("@Id", id));
        }

        private const string InsertSql = """
            INSERT INTO McpServers (Id, CreatedTimestamp, UpdatedTimestamp, Name, TransportType, Command, Arguments, Url, ApiKey, EnvironmentVariables, Description, IsEnabled)
            VALUES (@Id, @CreatedTimestamp, @UpdatedTimestamp, @Name, @TransportType, @Command, @Arguments, @Url, @ApiKey, @EnvironmentVariables, @Description, @IsEnabled)
            """;

        private static McpServerEntity ReadEntity(SqliteDataReader r) => new()
        {
            Id = r.GetString(r.GetOrdinal("Id")),
            CreatedTimestamp = r.GetInt64(r.GetOrdinal("CreatedTimestamp")),
            UpdatedTimestamp = r.GetInt64(r.GetOrdinal("UpdatedTimestamp")),
            Name = r.GetString(r.GetOrdinal("Name")),
            TransportType = r.GetString(r.GetOrdinal("TransportType")),
            Command = r.GetString(r.GetOrdinal("Command")),
            Arguments = JsonSerializer.Deserialize(r.GetString(r.GetOrdinal("Arguments")), EntityJsonContext.Default.ListString) ?? [],
            Url = r.GetString(r.GetOrdinal("Url")),
            ApiKey = r.GetString(r.GetOrdinal("ApiKey")),
            EnvironmentVariables = JsonSerializer.Deserialize(r.GetString(r.GetOrdinal("EnvironmentVariables")), EntityJsonContext.Default.DictionaryStringString) ?? [],
            Description = r.GetString(r.GetOrdinal("Description")),
            IsEnabled = r.GetBoolean(r.GetOrdinal("IsEnabled"))
        };

        private static void BindEntity(SqliteCommand cmd, McpServerEntity e)
        {
            cmd.Parameters.AddWithValue("@Id", e.Id);
            cmd.Parameters.AddWithValue("@CreatedTimestamp", e.CreatedTimestamp);
            cmd.Parameters.AddWithValue("@UpdatedTimestamp", e.UpdatedTimestamp);
            cmd.Parameters.AddWithValue("@Name", e.Name);
            cmd.Parameters.AddWithValue("@TransportType", e.TransportType);
            cmd.Parameters.AddWithValue("@Command", e.Command);
            cmd.Parameters.AddWithValue("@Arguments", JsonSerializer.Serialize(e.Arguments, EntityJsonContext.Default.ListString));
            cmd.Parameters.AddWithValue("@Url", e.Url);
            cmd.Parameters.AddWithValue("@ApiKey", e.ApiKey);
            cmd.Parameters.AddWithValue("@EnvironmentVariables", JsonSerializer.Serialize(e.EnvironmentVariables, EntityJsonContext.Default.DictionaryStringString));
            cmd.Parameters.AddWithValue("@Description", e.Description);
            cmd.Parameters.AddWithValue("@IsEnabled", e.IsEnabled);
        }
    }
}
