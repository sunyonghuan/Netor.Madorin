using Microsoft.Data.Sqlite;

using System;
using System.Collections.Generic;

namespace Netor.Cortana.Entitys.Services
{
    /// <summary>
    /// AI 服务提供商数据服务，提供对 AiProviders 表的增删改查操作。
    /// </summary>
    /// <param name="db">数据库上下文</param>
    public sealed class AiProviderService(CortanaDbContext db)
    {
        private readonly CortanaDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

        /// <summary>
        /// 获取所有已启用的 AI 服务提供商，按排序权重升序排列。
        /// </summary>
        public List<AiProviderEntity> GetAll()
        {
            return _db.Query(
                "SELECT * FROM AiProviders WHERE IsEnabled = 1 ORDER BY SortOrder, CreatedTimestamp DESC",
                ReadEntity);
        }

        /// <summary>
        /// 根据 ID 获取单个 AI 服务提供商。
        /// </summary>
        /// <param name="id">提供商 ID</param>
        /// <returns>提供商实体，不存在时返回 null</returns>
        public AiProviderEntity? GetById(string id)
        {
            return _db.QueryFirstOrDefault(
                "SELECT * FROM AiProviders WHERE Id = @Id",
                ReadEntity,
                cmd => cmd.Parameters.AddWithValue("@Id", id));
        }

        /// <summary>
        /// 添加一个新的 AI 服务提供商。
        /// </summary>
        /// <param name="entity">提供商实体</param>
        public void Add(AiProviderEntity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);

            entity.CreatedTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            entity.UpdatedTimestamp = entity.CreatedTimestamp;

            _db.Execute("""
                INSERT INTO AiProviders (Id, CreatedTimestamp, UpdatedTimestamp, Name, Url, Key, AuthToken, Description, ProviderType, IsDefault, IsEnabled, SortOrder)
                VALUES (@Id, @CreatedTimestamp, @UpdatedTimestamp, @Name, @Url, @Key, @AuthToken, @Description, @ProviderType, @IsDefault, @IsEnabled, @SortOrder)
                """,
                cmd => BindEntity(cmd, entity));
        }

        /// <summary>
        /// 更新已有的 AI 服务提供商。
        /// </summary>
        /// <param name="entity">提供商实体</param>
        public void Update(AiProviderEntity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);

            entity.UpdatedTimestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            _db.Execute("""
                UPDATE AiProviders SET
                    UpdatedTimestamp = @UpdatedTimestamp, Name = @Name, Url = @Url, Key = @Key, AuthToken = @AuthToken,
                    Description = @Description, ProviderType = @ProviderType,
                    IsDefault = @IsDefault, IsEnabled = @IsEnabled, SortOrder = @SortOrder
                WHERE Id = @Id
                """,
                cmd => BindEntity(cmd, entity));
        }

        /// <summary>
        /// 根据 ID 删除 AI 服务提供商。
        /// </summary>
        /// <param name="id">提供商 ID</param>
        public void Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id cannot be null or empty.", nameof(id));

            _db.Execute("DELETE FROM AiProviders WHERE Id = @Id",
                cmd => cmd.Parameters.AddWithValue("@Id", id));
        }

        /// <summary>
        /// 将指定服务提供商设为默认，同时清除其他提供商的默认标记。
        /// </summary>
        /// <param name="id">要设为默认的提供商 ID</param>
        public void SetDefault(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Id cannot be null or empty.", nameof(id));

            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            _db.Execute("UPDATE AiProviders SET IsDefault = 0, UpdatedTimestamp = @Now WHERE IsDefault = 1",
                cmd => cmd.Parameters.AddWithValue("@Now", now));

            _db.Execute("UPDATE AiProviders SET IsDefault = 1, UpdatedTimestamp = @Now WHERE Id = @Id",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@Now", now);
                    cmd.Parameters.AddWithValue("@Id", id);
                });
        }

        private static AiProviderEntity ReadEntity(SqliteDataReader r) => new()
        {
            Id = r.GetString(r.GetOrdinal("Id")),
            CreatedTimestamp = r.GetInt64(r.GetOrdinal("CreatedTimestamp")),
            UpdatedTimestamp = r.GetInt64(r.GetOrdinal("UpdatedTimestamp")),
            Name = r.GetString(r.GetOrdinal("Name")),
            Url = r.GetString(r.GetOrdinal("Url")),
            Key = r.GetString(r.GetOrdinal("Key")),
            AuthToken = r.GetString(r.GetOrdinal("AuthToken")),
            Description = r.GetString(r.GetOrdinal("Description")),
            ProviderType = r.GetString(r.GetOrdinal("ProviderType")),
            IsDefault = r.GetBoolean(r.GetOrdinal("IsDefault")),
            IsEnabled = r.GetBoolean(r.GetOrdinal("IsEnabled")),
            SortOrder = r.GetInt32(r.GetOrdinal("SortOrder"))
        };

        private static void BindEntity(SqliteCommand cmd, AiProviderEntity e)
        {
            cmd.Parameters.AddWithValue("@Id", e.Id);
            cmd.Parameters.AddWithValue("@CreatedTimestamp", e.CreatedTimestamp);
            cmd.Parameters.AddWithValue("@UpdatedTimestamp", e.UpdatedTimestamp);
            cmd.Parameters.AddWithValue("@Name", e.Name);
            cmd.Parameters.AddWithValue("@Url", e.Url);
            cmd.Parameters.AddWithValue("@Key", e.Key);
            cmd.Parameters.AddWithValue("@AuthToken", e.AuthToken);
            cmd.Parameters.AddWithValue("@Description", e.Description);
            cmd.Parameters.AddWithValue("@ProviderType", e.ProviderType);
            cmd.Parameters.AddWithValue("@IsDefault", e.IsDefault);
            cmd.Parameters.AddWithValue("@IsEnabled", e.IsEnabled);
            cmd.Parameters.AddWithValue("@SortOrder", e.SortOrder);
        }
    }
}
