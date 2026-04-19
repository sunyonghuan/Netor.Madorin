using Microsoft.Data.Sqlite;

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Netor.Cortana.Entitys.Services
{
    /// <summary>
    /// 系统设置服务，提供对 SystemSettings 表的读写操作。
    /// 支持泛型类型转换，并在首次启动时自动写入种子数据。
    /// </summary>
    public sealed class SystemSettingsService
    {
        private readonly CortanaDbContext _db;

        /// <summary>
        /// 初始化系统设置服务。
        /// </summary>
        /// <param name="db">数据库上下文</param>
        public SystemSettingsService(CortanaDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// 获取指定键的设置值，不存在时返回 null。
        /// </summary>
        /// <param name="key">设置键名，格式如 "SherpaOnnx.KeywordsThreshold"</param>
        public string? GetValue(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or empty.", nameof(key));

            return _db.ExecuteScalar<string>(
                "SELECT Value FROM SystemSettings WHERE Id = @Id",
                cmd => cmd.Parameters.AddWithValue("@Id", key));
        }

        /// <summary>
        /// 获取指定键的设置值，不存在时返回指定默认值。
        /// </summary>
        /// <param name="key">设置键名</param>
        /// <param name="defaultValue">默认值</param>
        public string GetValue(string key, string defaultValue)
        {
            return GetValue(key) ?? defaultValue;
        }

        /// <summary>
        /// 获取指定键的强类型设置值，转换失败或不存在时返回默认值。
        /// 支持 float、double、int、long、bool、string 类型。
        /// </summary>
        /// <typeparam name="T">目标类型</typeparam>
        /// <param name="key">设置键名</param>
        /// <param name="defaultValue">默认值</param>
        public T GetValue<T>(string key, T defaultValue)
        {
            var raw = GetValue(key);
            if (raw is null)
                return defaultValue;

            try
            {
                if (typeof(T) == typeof(string)) return (T)(object)raw;
                if (typeof(T) == typeof(int) && int.TryParse(raw, CultureInfo.InvariantCulture, out var i)) return (T)(object)i;
                if (typeof(T) == typeof(long) && long.TryParse(raw, CultureInfo.InvariantCulture, out var l)) return (T)(object)l;
                if (typeof(T) == typeof(float) && float.TryParse(raw, CultureInfo.InvariantCulture, out var f)) return (T)(object)f;
                if (typeof(T) == typeof(double) && double.TryParse(raw, CultureInfo.InvariantCulture, out var d)) return (T)(object)d;
                if (typeof(T) == typeof(bool) && bool.TryParse(raw, out var b)) return (T)(object)b;
                return defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// 设置指定键的值，键不存在时自动插入，存在时更新。
        /// </summary>
        /// <param name="key">设置键名</param>
        /// <param name="value">设置值</param>
        public void SetValue(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or empty.", nameof(key));

            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            var existing = _db.QueryFirstOrDefault(
                "SELECT Id FROM SystemSettings WHERE Id = @Id",
                r => r.GetString(r.GetOrdinal("Id")),
                cmd => cmd.Parameters.AddWithValue("@Id", key));

            if (existing is not null)
            {
                _db.Execute(
                    "UPDATE SystemSettings SET Value = @Value, UpdatedTimestamp = @Now WHERE Id = @Id",
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@Value", value);
                        cmd.Parameters.AddWithValue("@Now", now);
                        cmd.Parameters.AddWithValue("@Id", key);
                    });
            }
            else
            {
                _db.Execute(
                    """
                    INSERT INTO SystemSettings (Id, CreatedTimestamp, UpdatedTimestamp, [Group], DisplayName, Description, Value, DefaultValue, ValueType, SortOrder)
                    VALUES (@Id, @Now, @Now, '', '', '', @Value, @Value, 'string', 0)
                    """,
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@Id", key);
                        cmd.Parameters.AddWithValue("@Now", now);
                        cmd.Parameters.AddWithValue("@Value", value);
                    });
            }
        }

        /// <summary>
        /// 按分组获取该组内所有设置，按 SortOrder 升序排列。
        /// </summary>
        /// <param name="group">分组名称</param>
        public List<SystemSettingsEntity> GetByGroup(string group)
        {
            return _db.Query(
                "SELECT * FROM SystemSettings WHERE [Group] = @Group ORDER BY SortOrder",
                ReadEntity,
                cmd => cmd.Parameters.AddWithValue("@Group", group));
        }

        /// <summary>
        /// 获取全部设置，按分组和 SortOrder 排列。
        /// </summary>
        public List<SystemSettingsEntity> GetAll()
        {
            return _db.Query(
                "SELECT * FROM SystemSettings ORDER BY [Group], SortOrder",
                ReadEntity);
        }

        /// <summary>
        /// 批量保存设置（前端提交用），仅更新 Value 字段，保留其他元数据不变。
        /// </summary>
        /// <param name="updates">键值更新列表</param>
        public void SaveBatch(IEnumerable<(string Key, string Value)> updates)
        {
            ArgumentNullException.ThrowIfNull(updates);

            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            _db.ExecuteInTransaction(conn =>
            {
                foreach (var (key, value) in updates)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "UPDATE SystemSettings SET Value = @Value, UpdatedTimestamp = @Now WHERE Id = @Id";
                    cmd.Parameters.AddWithValue("@Value", value);
                    cmd.Parameters.AddWithValue("@Now", now);
                    cmd.Parameters.AddWithValue("@Id", key);
                    cmd.ExecuteNonQuery();
                }
            });
        }

        /// <summary>
        /// 将指定键的值重置为其默认值。
        /// </summary>
        /// <param name="key">设置键名</param>
        public void ResetToDefault(string key)
        {
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            _db.Execute(
                "UPDATE SystemSettings SET Value = DefaultValue, UpdatedTimestamp = @Now WHERE Id = @Id",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@Now", now);
                    cmd.Parameters.AddWithValue("@Id", key);
                });
        }

        /// <summary>
        /// 将所有设置重置为默认值。
        /// </summary>
        public void ResetAllToDefault()
        {
            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            _db.Execute(
                "UPDATE SystemSettings SET Value = DefaultValue, UpdatedTimestamp = @Now",
                cmd => cmd.Parameters.AddWithValue("@Now", now));
        }

        /// <summary>
        /// 首次启动时写入种子数据。
        /// 若数据库中 SystemSettings 表非空则跳过，保证幂等性。
        /// </summary>
        /// <param name="sherpaOnnx">SherpaOnnx 默认配置（来自嵌入的 appsettings.json）</param>
        /// <param name="ttsSpeed">TTS 语速默认值</param>
        /// <param name="workspaceDirectory">工作目录默认值</param>
        public void EnsureSeedData(
            SherpaOnnxSeedValues sherpaOnnx,
            float ttsSpeed,
            string workspaceDirectory)
        {
            var count = _db.ExecuteScalar<long>("SELECT COUNT(1) FROM SystemSettings");
            if (count > 0)
                return;

            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            var seeds = new List<SystemSettingsEntity>
            {
                // ── 系统 ──────────────────────────────
                Seed("System.WorkspaceDirectory",
                    group: "系统", displayName: "当前工作目录",
                    description: "应用数据和模型文件的存储目录，保存后立即生效。",
                    value: workspaceDirectory, valueType: "string", sortOrder: 0),

                // ── 语音唤醒 (KWS) ────────────────────
                Seed("SherpaOnnx.KeywordsThreshold",
                    group: "语音唤醒", displayName: "唤醒词灵敏度阈值",
                    description: "值越小越灵敏，建议范围 0.01~0.5。",
                    value: sherpaOnnx.KeywordsThreshold.ToString(CultureInfo.InvariantCulture),
                    valueType: "float", sortOrder: 0),

                Seed("SherpaOnnx.KeywordsScore",
                    group: "语音唤醒", displayName: "唤醒词增强分数",
                    description: "值越大越容易触发唤醒，建议范围 1.0~20.0。",
                    value: sherpaOnnx.KeywordsScore.ToString(CultureInfo.InvariantCulture),
                    valueType: "float", sortOrder: 1),

                Seed("SherpaOnnx.NumTrailingBlanks",
                    group: "语音唤醒", displayName: "尾部空白帧数",
                    description: "唤醒确认所需的静默帧数，值越小响应越快。",
                    value: sherpaOnnx.NumTrailingBlanks.ToString(CultureInfo.InvariantCulture),
                    valueType: "int", sortOrder: 2),

                // ── 语音识别 (STT) ────────────────────
                Seed("SherpaOnnx.Rule1MinTrailingSilence",
                    group: "语音识别", displayName: "无语音静音超时(秒)",
                    description: "未检测到任何语音时的静音超时，超过后视为端点。",
                    value: sherpaOnnx.Rule1MinTrailingSilence.ToString(CultureInfo.InvariantCulture),
                    valueType: "float", sortOrder: 0),

                Seed("SherpaOnnx.Rule2MinTrailingSilence",
                    group: "语音识别", displayName: "说话停顿超时(秒)",
                    description: "检测到语音后的停顿超时，超过后视为一句话结束。",
                    value: sherpaOnnx.Rule2MinTrailingSilence.ToString(CultureInfo.InvariantCulture),
                    valueType: "float", sortOrder: 1),

                Seed("SherpaOnnx.Rule3MinUtteranceLength",
                    group: "语音识别", displayName: "单次语音最大时长(秒)",
                    description: "单次语音的最大录音时长，超过后强制结束识别。",
                    value: sherpaOnnx.Rule3MinUtteranceLength.ToString(CultureInfo.InvariantCulture),
                    valueType: "float", sortOrder: 2),

                Seed("SherpaOnnx.RecognitionTimeoutSeconds",
                    group: "语音识别", displayName: "识别空闲超时(秒)",
                    description: "识别启动后等待说话的超时时间，超过后自动停止识别。",
                    value: sherpaOnnx.RecognitionTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                    valueType: "float", sortOrder: 3),

                // ── 语音合成 (TTS) ────────────────────
                Seed("Tts.Speed",
                    group: "语音合成", displayName: "语速倍率",
                    description: "1.0 为正常速度，支持实时调整无需重启。",
                    value: ttsSpeed.ToString(CultureInfo.InvariantCulture),
                    valueType: "float", sortOrder: 0),

                Seed("Tts.WelcomeGreeting",
                    group: "语音合成", displayName: "唤醒欢迎语",
                    description: "AI 被唤醒时播放的欢迎语，修改后需要重启应用才能生效。",
                    value: "主人，我在!", valueType: "string", sortOrder: 1),

                // ── 对话历史 ──────────────────────────
                Seed("Compaction.ModelId",
                    group: "对话历史", displayName: "缩略专用模型",
                    description: "用于会话压缩摘要的模型，留空则跟随当前对话模型。",
                    value: "", valueType: "model", sortOrder: 0),

                Seed("Compaction.SegmentSize",
                    group: "对话历史", displayName: "压缩段落大小",
                    description: "每多少条消息生成一个压缩摘要段落（建议 20-50）。",
                    value: "30", valueType: "int", sortOrder: 1),

                Seed("Compaction.RawTailSize",
                    group: "对话历史", displayName: "尾部原始消息数",
                    description: "保留最近多少条原始消息不压缩，确保 AI 看到完整的近期对话细节。",
                    value: "20", valueType: "int", sortOrder: 2),

                Seed("Compaction.MaxDisplaySegments",
                    group: "对话历史", displayName: "最大显示段落数",
                    description: "加载历史时最多携带多少个摘要段落，超出的旧段落不再加载（但不删除）。",
                    value: "15", valueType: "int", sortOrder: 3),

                // ── 网络 ──────────────────────────────
                Seed("WebSocket.Port",
                    group: "网络", displayName: "WebSocket 端口",
                    description: "WebSocket 服务监听端口，修改后立即生效，建议重启软件以确保插件正常工作。",
                    value: "12841", valueType: "int", sortOrder: 0),
            };

            _db.ExecuteInTransaction(conn =>
            {
                foreach (var entity in seeds)
                {
                    entity.CreatedTimestamp = now;
                    entity.UpdatedTimestamp = now;

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = InsertSql;
                    BindEntity(cmd, entity);
                    cmd.ExecuteNonQuery();
                }
            });
        }

        // ── 私有辅助 ──────────────────────────────────

        /// <summary>
        /// 确保指定设置项存在，不存在时按完整元数据插入。
        /// 用于版本升级时补充新增的设置项（EnsureSeedData 仅在空表时执行）。
        /// </summary>
        public void EnsureSetting(
            string key,
            string group,
            string displayName,
            string description,
            string defaultValue,
            string valueType,
            int sortOrder)
        {
            if (GetValue(key) is not null) return;

            var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            var entity = Seed(key, group, displayName, description, defaultValue, valueType, sortOrder);
            entity.CreatedTimestamp = now;
            entity.UpdatedTimestamp = now;

            _db.Execute(InsertSql, cmd => BindEntity(cmd, entity));
        }

        /// <summary>
        /// 删除指定键的设置项（用于版本升级时移除废弃配置）。
        /// </summary>
        public void DeleteSetting(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            _db.Execute("DELETE FROM SystemSettings WHERE Id = @Id",
                cmd => cmd.Parameters.AddWithValue("@Id", key));
        }

        private static SystemSettingsEntity Seed(
            string key,
            string group,
            string displayName,
            string description,
            string value,
            string valueType,
            int sortOrder)
        {
            return new SystemSettingsEntity
            {
                Id = key,
                Group = group,
                DisplayName = displayName,
                Description = description,
                Value = value,
                DefaultValue = value,
                ValueType = valueType,
                SortOrder = sortOrder
            };
        }

        private const string InsertSql = """
            INSERT INTO SystemSettings (Id, CreatedTimestamp, UpdatedTimestamp, [Group], DisplayName, Description, Value, DefaultValue, ValueType, SortOrder)
            VALUES (@Id, @CreatedTimestamp, @UpdatedTimestamp, @Group, @DisplayName, @Description, @Value, @DefaultValue, @ValueType, @SortOrder)
            """;

        private static SystemSettingsEntity ReadEntity(SqliteDataReader r) => new()
        {
            Id = r.GetString(r.GetOrdinal("Id")),
            CreatedTimestamp = r.GetInt64(r.GetOrdinal("CreatedTimestamp")),
            UpdatedTimestamp = r.GetInt64(r.GetOrdinal("UpdatedTimestamp")),
            Group = r.GetString(r.GetOrdinal("Group")),
            DisplayName = r.GetString(r.GetOrdinal("DisplayName")),
            Description = r.GetString(r.GetOrdinal("Description")),
            Value = r.GetString(r.GetOrdinal("Value")),
            DefaultValue = r.GetString(r.GetOrdinal("DefaultValue")),
            ValueType = r.GetString(r.GetOrdinal("ValueType")),
            SortOrder = r.GetInt32(r.GetOrdinal("SortOrder"))
        };

        private static void BindEntity(SqliteCommand cmd, SystemSettingsEntity e)
        {
            cmd.Parameters.AddWithValue("@Id", e.Id);
            cmd.Parameters.AddWithValue("@CreatedTimestamp", e.CreatedTimestamp);
            cmd.Parameters.AddWithValue("@UpdatedTimestamp", e.UpdatedTimestamp);
            cmd.Parameters.AddWithValue("@Group", e.Group);
            cmd.Parameters.AddWithValue("@DisplayName", e.DisplayName);
            cmd.Parameters.AddWithValue("@Description", e.Description);
            cmd.Parameters.AddWithValue("@Value", e.Value);
            cmd.Parameters.AddWithValue("@DefaultValue", e.DefaultValue);
            cmd.Parameters.AddWithValue("@ValueType", e.ValueType);
            cmd.Parameters.AddWithValue("@SortOrder", e.SortOrder);
        }
    }

    /// <summary>
    /// EnsureSeedData 所需的 SherpaOnnx 默认配置值载体。
    /// 由调用方从 AppSettings 中提取后传入，避免 Entitys 项目直接依赖 AppSettings。
    /// </summary>
    public sealed class SherpaOnnxSeedValues
    {
        public float KeywordsThreshold { get; set; }
        public float KeywordsScore { get; set; }
        public int NumTrailingBlanks { get; set; }
        public float Rule1MinTrailingSilence { get; set; }
        public float Rule2MinTrailingSilence { get; set; }
        public float Rule3MinUtteranceLength { get; set; }
        public float RecognitionTimeoutSeconds { get; set; }
    }
}
