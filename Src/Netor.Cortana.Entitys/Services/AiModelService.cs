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
        /// 获取指定提供商下的全部模型，包括已关闭的模型。
        /// </summary>
        /// <param name="providerId">AI 服务提供商 ID</param>
        public List<AiModelEntity> GetAllByProviderId(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                throw new ArgumentException("ProviderId cannot be null or empty.", nameof(providerId));

            return _db.Query(
                "SELECT * FROM AiModels WHERE ProviderId = @ProviderId ORDER BY Name",
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
                    if (m.IsDefault)
                        m.IsEnabled = true;
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
        /// 按提供商和模型名称增量同步远端模型，保留已有模型的本地 ID 和状态。
        /// </summary>
        /// <param name="providerId">AI 服务提供商 ID</param>
        /// <param name="remoteModels">远端成功返回的模型列表</param>
        public List<AiModelEntity> SyncByProviderId(string providerId, IEnumerable<AiModelEntity> remoteModels)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                throw new ArgumentException("ProviderId cannot be null or empty.", nameof(providerId));
            ArgumentNullException.ThrowIfNull(remoteModels);

            var incoming = new List<AiModelEntity>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in remoteModels)
            {
                ArgumentNullException.ThrowIfNull(model);
                if (string.IsNullOrWhiteSpace(model.Name) || !names.Add(model.Name))
                    continue;

                model.ProviderId = providerId;
                incoming.Add(model);
            }

            _db.ExecuteInTransaction((conn, transaction) =>
            {
                var existing = ReadByProvider(conn, transaction, providerId);
                var existingByName = new Dictionary<string, AiModelEntity>(StringComparer.Ordinal);
                foreach (var model in existing)
                {
                    existingByName.TryAdd(model.Name, model);
                }
                var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

                foreach (var remote in incoming)
                {
                    if (existingByName.TryGetValue(remote.Name, out var local))
                    {
                        UpdateRemoteMetadata(conn, transaction, local, remote, now);
                        continue;
                    }

                    remote.Id = Guid.NewGuid().ToString("N");
                    remote.CreatedTimestamp = now;
                    remote.UpdatedTimestamp = now;
                    remote.IsEnabled = true;
                    remote.IsDefault = false;
                    Insert(conn, transaction, remote);
                }

                var stale = existing
                    .Where(model => !names.Contains(model.Name))
                    .ToList();
                var deletedDefault = stale.Any(static model => model.IsDefault);
                if (deletedDefault)
                {
                    var candidates = ReadEnabled(conn, transaction)
                        .Where(model => !stale.Any(staleModel => staleModel.Id == model.Id))
                        .OrderBy(model => string.Equals(model.ProviderId, providerId, StringComparison.Ordinal) ? 0 : 1)
                        .ThenBy(model => model.Name, StringComparer.Ordinal)
                        .ToList();

                    SetAllDefaults(conn, transaction, now);
                    if (candidates.Count > 0)
                        SetDefaultInternal(conn, transaction, candidates[0].Id, now);
                }

                foreach (var model in stale)
                {
                    DeleteInternal(conn, transaction, model.Id);
                }
                return 0;
            });

            return GetAllByProviderId(providerId);
        }

        /// <summary>
        /// 删除指定提供商下的所有模型。
        /// </summary>
        /// <param name="providerId">AI 服务提供商 ID</param>
        public int DeleteByProviderId(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                throw new ArgumentException("ProviderId cannot be null or empty.", nameof(providerId));

            var before = GetAllByProviderId(providerId).Count;
            _db.ExecuteInTransaction((conn, transaction) =>
            {
                DeleteByProviderIdInternal(conn, transaction, providerId);
                return 0;
            });
            return before;
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

            _db.ExecuteInTransaction((conn, transaction) =>
            {
                var model = ReadById(conn, transaction, id)
                    ?? throw new InvalidOperationException($"AI model '{id}' was not found.");
                SetAllDefaults(conn, transaction, now);
                SetDefaultInternal(conn, transaction, model.Id, now);
                return 0;
            });
        }

        /// <summary>
        /// 设置模型启用状态。默认模型不能被关闭。
        /// </summary>
        /// <param name="id">模型 ID</param>
        /// <param name="isEnabled">是否启用</param>
        public void SetEnabled(string id, bool isEnabled)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Id cannot be null or empty.", nameof(id));

            _db.ExecuteInTransaction((conn, transaction) =>
            {
                var model = ReadById(conn, transaction, id)
                    ?? throw new InvalidOperationException($"AI model '{id}' was not found.");
                if (!isEnabled && model.IsDefault)
                    throw new InvalidOperationException("The default AI model cannot be disabled.");

                UpdateEnabled(conn, transaction, id, isEnabled, DateTimeOffset.Now.ToUnixTimeMilliseconds());
                return 0;
            });
        }

        /// <summary>
        /// 按提供商批量设置模型启用状态。批量关闭时默认模型保持开启。
        /// </summary>
        /// <param name="providerId">AI 服务提供商 ID</param>
        /// <param name="isEnabled">是否启用</param>
        public void SetEnabledByProviderId(string providerId, bool isEnabled)
        {
            if (string.IsNullOrWhiteSpace(providerId))
                throw new ArgumentException("ProviderId cannot be null or empty.", nameof(providerId));

            _db.ExecuteInTransaction((conn, transaction) =>
            {
                using var command = conn.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = isEnabled
                    ? "UPDATE AiModels SET IsEnabled = 1, UpdatedTimestamp = @Now WHERE ProviderId = @ProviderId"
                    : "UPDATE AiModels SET IsEnabled = 0, UpdatedTimestamp = @Now WHERE ProviderId = @ProviderId AND IsDefault = 0";
                command.Parameters.AddWithValue("@Now", DateTimeOffset.Now.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("@ProviderId", providerId);
                command.ExecuteNonQuery();
                return 0;
            });
        }

        /// <summary>
        /// 添加单个模型。
        /// </summary>
        /// <param name="entity">模型实体</param>
        public void Add(AiModelEntity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);

            if (entity.IsDefault)
                entity.IsEnabled = true;

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

            if (entity.IsDefault)
                entity.IsEnabled = true;

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

            _db.ExecuteInTransaction((conn, transaction) =>
            {
                var model = ReadById(conn, transaction, id);
                if (model is null)
                    return 0;

                if (model.IsDefault)
                    ReplaceDefault(conn, transaction, model.Id, null, model.ProviderId, DateTimeOffset.Now.ToUnixTimeMilliseconds());

                DeleteInternal(conn, transaction, id);
                return 0;
            });
        }

        private void DeleteByProviderIdInternal(SqliteConnection conn, SqliteTransaction transaction, string providerId)
        {
            var models = ReadByProvider(conn, transaction, providerId);
            if (models.Any(static model => model.IsDefault))
                ReplaceDefault(conn, transaction, null, providerId, providerId, DateTimeOffset.Now.ToUnixTimeMilliseconds());

            foreach (var model in models)
                DeleteInternal(conn, transaction, model.Id);
        }

        private static void ReplaceDefault(
            SqliteConnection conn,
            SqliteTransaction transaction,
            string? excludedId,
            string? excludedProviderId,
            string providerId,
            long now)
        {
            var candidates = ReadEnabled(conn, transaction)
                .Where(model => excludedId is null || model.Id != excludedId)
                .Where(model => excludedProviderId is null || !string.Equals(model.ProviderId, excludedProviderId, StringComparison.Ordinal))
                .OrderBy(model => string.Equals(model.ProviderId, providerId, StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(model => model.Name, StringComparer.Ordinal)
                .ToList();

            SetAllDefaults(conn, transaction, now);
            if (candidates.Count > 0)
                SetDefaultInternal(conn, transaction, candidates[0].Id, now);
        }

        private static void SetAllDefaults(SqliteConnection conn, SqliteTransaction transaction, long now)
        {
            using var command = conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE AiModels SET IsDefault = 0, UpdatedTimestamp = @Now WHERE IsDefault = 1";
            command.Parameters.AddWithValue("@Now", now);
            command.ExecuteNonQuery();
        }

        private static void SetDefaultInternal(SqliteConnection conn, SqliteTransaction transaction, string id, long now)
        {
            using var command = conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE AiModels SET IsDefault = 1, IsEnabled = 1, UpdatedTimestamp = @Now WHERE Id = @Id";
            command.Parameters.AddWithValue("@Now", now);
            command.Parameters.AddWithValue("@Id", id);
            command.ExecuteNonQuery();
        }

        private static void UpdateEnabled(SqliteConnection conn, SqliteTransaction transaction, string id, bool isEnabled, long now)
        {
            using var command = conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE AiModels SET IsEnabled = @IsEnabled, UpdatedTimestamp = @Now WHERE Id = @Id";
            command.Parameters.AddWithValue("@IsEnabled", isEnabled);
            command.Parameters.AddWithValue("@Now", now);
            command.Parameters.AddWithValue("@Id", id);
            command.ExecuteNonQuery();
        }

        private static void DeleteInternal(SqliteConnection conn, SqliteTransaction transaction, string id)
        {
            using var command = conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM AiModels WHERE Id = @Id";
            command.Parameters.AddWithValue("@Id", id);
            command.ExecuteNonQuery();
        }

        private static void UpdateRemoteMetadata(SqliteConnection conn, SqliteTransaction transaction, AiModelEntity local, AiModelEntity remote, long now)
        {
            using var command = conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE AiModels SET
                    UpdatedTimestamp = @UpdatedTimestamp, Name = @Name, DisplayName = @DisplayName,
                    Description = @Description, ContextLength = @ContextLength, ModelType = @ModelType,
                    InputCapabilities = @InputCapabilities, OutputCapabilities = @OutputCapabilities,
                    InteractionCapabilities = @InteractionCapabilities, CapabilitySource = @CapabilitySource,
                    CapabilityNotes = @CapabilityNotes
                WHERE Id = @Id
                """;
            command.Parameters.AddWithValue("@UpdatedTimestamp", now);
            command.Parameters.AddWithValue("@Name", remote.Name);
            command.Parameters.AddWithValue("@DisplayName", remote.DisplayName);
            command.Parameters.AddWithValue("@Description", remote.Description);
            command.Parameters.AddWithValue("@ContextLength", remote.ContextLength);
            command.Parameters.AddWithValue("@ModelType", remote.ModelType);
            command.Parameters.AddWithValue("@InputCapabilities", (int)remote.InputCapabilities);
            command.Parameters.AddWithValue("@OutputCapabilities", (int)remote.OutputCapabilities);
            command.Parameters.AddWithValue("@InteractionCapabilities", (int)remote.InteractionCapabilities);
            command.Parameters.AddWithValue("@CapabilitySource", remote.CapabilitySource);
            command.Parameters.AddWithValue("@CapabilityNotes", remote.CapabilityNotes);
            command.Parameters.AddWithValue("@Id", local.Id);
            command.ExecuteNonQuery();
        }

        private static void Insert(SqliteConnection conn, SqliteTransaction transaction, AiModelEntity entity)
        {
            using var command = conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = InsertSql;
            BindEntity(command, entity);
            command.ExecuteNonQuery();
        }

        private static List<AiModelEntity> ReadByProvider(SqliteConnection conn, SqliteTransaction transaction, string providerId)
            => ReadList(conn, transaction, "SELECT * FROM AiModels WHERE ProviderId = @ProviderId ORDER BY Name", command => command.Parameters.AddWithValue("@ProviderId", providerId));

        private static List<AiModelEntity> ReadEnabled(SqliteConnection conn, SqliteTransaction transaction)
            => ReadList(conn, transaction, "SELECT * FROM AiModels WHERE IsEnabled = 1 ORDER BY Name", null);

        private static AiModelEntity? ReadById(SqliteConnection conn, SqliteTransaction transaction, string id)
            => ReadList(conn, transaction, "SELECT * FROM AiModels WHERE Id = @Id", command => command.Parameters.AddWithValue("@Id", id)).FirstOrDefault();

        private static List<AiModelEntity> ReadList(SqliteConnection conn, SqliteTransaction transaction, string sql, Action<SqliteCommand>? bind)
        {
            using var command = conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            bind?.Invoke(command);
            using var reader = command.ExecuteReader();
            var result = new List<AiModelEntity>();
            while (reader.Read())
                result.Add(ReadEntity(reader));
            return result;
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
