using Microsoft.Data.Sqlite;

using System;
using System.Collections.Generic;

namespace Netor.Cortana.Entitys.Services
{
    /// <summary>
    /// AI 模型数据服务，提供对 AiModels 表的增删改查操作。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    public sealed class AiModelService(CortanaDbContext db)
    {
        private readonly CortanaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

        /// <summary>
        /// 获取指定提供商下所有已启用的模型，按名称排序。
        /// </summary>
        /// <param name="providerId">AI 服务提供商 ID</param>
        public List<AiModelEntity> GetByProviderId(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                throw new ArgumentException("ProviderId cannot be null or empty.", nameof(providerId));

            return _db.Query(
                "SELECT * FROM AiModels WHERE ProviderId = @ProviderId AND IsEnabled = 1 ORDER BY Name",
                ReadEntity,
                cmd => cmd.Parameters.AddWithValue("@ProviderId", providerId));
        }

        /// <summary>
        /// 检查指定提供商是否已有模型数据。
        /// </summary>
        /// <param name="providerId">AI 服务提供商 ID</param>
        public bool HasModels(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                return false;

            var count = _db.ExecuteScalar<long>(
                "SELECT COUNT(1) FROM AiModels WHERE ProviderId = @ProviderId",
                cmd => cmd.Parameters.AddWithValue("@ProviderId", providerId));

            return count > 0;
        }

        /// <summary>
        /// 批量插入模型数据。
        /// </summary>
        /// <param name="models">模型实体集合</param>
        public int BatchInsert(IEnumerable<AiModelEntity> models)
        {
            ArgumentNullException.ThrowIfNull(models);

            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var count = 0;

            _db.ExecuteInTransaction(conn =>
            {
                foreach (var m in models)
                {
                    m.CreatedTimestamp = now;
                    m.UpdatedTimestamp = now;

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = InsertSql;
                    BindEntity(cmd, m);
                    cmd.ExecuteNonQuery();
                    count++;
                }
            });

            return count;
        }

        /// <summary>
        /// 删除指定提供商下的所有模型。
        /// </summary>
        /// <param name="providerId">AI 服务提供商 ID</param>
        public int DeleteByProviderId(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                throw new ArgumentException("ProviderId cannot be null or empty.", nameof(providerId));

            return _db.Execute(
                "DELETE FROM AiModels WHERE ProviderId = @ProviderId",
                cmd => cmd.Parameters.AddWithValue("@ProviderId", providerId));
        }

        /// <summary>
        /// 根据 ID 获取单个模型。
        /// </summary>
        /// <param name="id">模型 ID</param>
        public AiModelEntity? GetById(string id)
        {
            return _db.QueryFirstOrDefault(
                "SELECT * FROM AiModels WHERE Id = @Id",
                ReadEntity,
                cmd => cmd.Parameters.AddWithValue("@Id", id));
        }

        /// <summary>
        /// 将指定模型设为默认，同时清除其他模型的默认标记。
        /// </summary>
        /// <param name="id">要设为默认的模型 ID</param>
        public void SetDefault(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Id cannot be null or empty.", nameof(id));

            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            _db.Execute("UPDATE AiModels SET IsDefault = 0, UpdatedTimestamp = @Now WHERE IsDefault = 1",
                cmd => cmd.Parameters.AddWithValue("@Now", now));

            _db.Execute("UPDATE AiModels SET IsDefault = 1, UpdatedTimestamp = @Now WHERE Id = @Id",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@Now", now);
                    cmd.Parameters.AddWithValue("@Id", id);
                });
        }

        /// <summary>
        /// 添加单个模型。
        /// </summary>
        /// <param name="entity">模型实体</param>
        public void Add(AiModelEntity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);

            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            entity.CreatedTimestamp = now;
            entity.UpdatedTimestamp = now;

            _db.Execute(InsertSql, cmd => BindEntity(cmd, entity));
        }

        /// <summary>
        /// 更新已有的模型。
        /// </summary>
        /// <param name="entity">模型实体</param>
        public void Update(AiModelEntity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);

            entity.UpdatedTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            _db.Execute("""
                UPDATE AiModels SET
                    UpdatedTimestamp = @UpdatedTimestamp, Name = @Name, DisplayName = @DisplayName,
                    Description = @Description, ContextLength = @ContextLength, ModelType = @ModelType,
                    IsDefault = @IsDefault, IsEnabled = @IsEnabled, ProviderId = @ProviderId,
                    InputCapabilities = @InputCapabilities, OutputCapabilities = @OutputCapabilities,
                    InteractionCapabilities = @InteractionCapabilities, CapabilitySource = @CapabilitySource,
                    CapabilityNotes = @CapabilityNotes
                WHERE Id = @Id
                """,
                cmd => BindEntity(cmd, entity));
        }

        /// <summary>
        /// 根据 ID 删除单个模型。
        /// </summary>
        /// <param name="id">模型 ID</param>
        public void Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Id cannot be null or empty.", nameof(id));

            _db.Execute("DELETE FROM AiModels WHERE Id = @Id",
                cmd => cmd.Parameters.AddWithValue("@Id", id));
        }

        private const string InsertSql = """
            INSERT INTO AiModels (Id, CreatedTimestamp, UpdatedTimestamp, Name, DisplayName, Description, ContextLength, ModelType, IsDefault, IsEnabled, ProviderId, InputCapabilities, OutputCapabilities, InteractionCapabilities, CapabilitySource, CapabilityNotes)
            VALUES (@Id, @CreatedTimestamp, @UpdatedTimestamp, @Name, @DisplayName, @Description, @ContextLength, @ModelType, @IsDefault, @IsEnabled, @ProviderId, @InputCapabilities, @OutputCapabilities, @InteractionCapabilities, @CapabilitySource, @CapabilityNotes)
            """;

        private static AiModelEntity ReadEntity(SqliteDataReader r) => new()
        {
            Id = r.GetString(r.GetOrdinal("Id")),
            CreatedTimestamp = r.GetInt64(r.GetOrdinal("CreatedTimestamp")),
            UpdatedTimestamp = r.GetInt64(r.GetOrdinal("UpdatedTimestamp")),
            Name = r.GetString(r.GetOrdinal("Name")),
            DisplayName = r.GetString(r.GetOrdinal("DisplayName")),
            Description = r.GetString(r.GetOrdinal("Description")),
            ContextLength = r.GetInt32(r.GetOrdinal("ContextLength")),
            ModelType = r.GetString(r.GetOrdinal("ModelType")),
            IsDefault = r.GetBoolean(r.GetOrdinal("IsDefault")),
            IsEnabled = r.GetBoolean(r.GetOrdinal("IsEnabled")),
            ProviderId = r.GetString(r.GetOrdinal("ProviderId")),
            InputCapabilities = (InputCapabilities)r.GetInt32(r.GetOrdinal("InputCapabilities")),
            OutputCapabilities = (OutputCapabilities)r.GetInt32(r.GetOrdinal("OutputCapabilities")),
            InteractionCapabilities = (InteractionCapabilities)r.GetInt32(r.GetOrdinal("InteractionCapabilities")),
            CapabilitySource = r.GetString(r.GetOrdinal("CapabilitySource")),
            CapabilityNotes = r.GetString(r.GetOrdinal("CapabilityNotes")),
        };

        private static void BindEntity(SqliteCommand cmd, AiModelEntity e)
        {
            cmd.Parameters.AddWithValue("@Id", e.Id);
            cmd.Parameters.AddWithValue("@CreatedTimestamp", e.CreatedTimestamp);
            cmd.Parameters.AddWithValue("@UpdatedTimestamp", e.UpdatedTimestamp);
            cmd.Parameters.AddWithValue("@Name", e.Name);
            cmd.Parameters.AddWithValue("@DisplayName", e.DisplayName);
            cmd.Parameters.AddWithValue("@Description", e.Description);
            cmd.Parameters.AddWithValue("@ContextLength", e.ContextLength);
            cmd.Parameters.AddWithValue("@ModelType", e.ModelType);
            cmd.Parameters.AddWithValue("@IsDefault", e.IsDefault);
            cmd.Parameters.AddWithValue("@IsEnabled", e.IsEnabled);
            cmd.Parameters.AddWithValue("@ProviderId", e.ProviderId);
            cmd.Parameters.AddWithValue("@InputCapabilities", (int)e.InputCapabilities);
            cmd.Parameters.AddWithValue("@OutputCapabilities", (int)e.OutputCapabilities);
            cmd.Parameters.AddWithValue("@InteractionCapabilities", (int)e.InteractionCapabilities);
            cmd.Parameters.AddWithValue("@CapabilitySource", e.CapabilitySource);
            cmd.Parameters.AddWithValue("@CapabilityNotes", e.CapabilityNotes);
        }
    }
}
