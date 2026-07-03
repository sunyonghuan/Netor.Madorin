using System;

namespace Netor.Cortana.Entitys
{
    /// <summary>
    /// 基础实体类型，为所有数据库实体提供通用的主键标识与时间戳字段。
    /// 所有业务实体均应继承此类以保持一致的主键策略和创建时间记录。
    /// </summary>
    public abstract class BaseEntity
    {
        /// <summary>
        /// 全局唯一标识符（主键），以无连字符的 GUID 字符串表示。
        /// 在实例化时自动生成，无需手动赋值。
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>
        /// 创建时间戳，表示实体创建时的 UTC 毫秒数（自 Unix 纪元 1970-01-01 起）。
        /// 以 long 类型存储，便于排序、索引和跨时区处理。
        /// </summary>
        public long CreatedTimestamp { get; set; } = DateTimeOffset.Now.ToUnixTimeMilliseconds();

        /// <summary>
        /// 最后更新时间戳，表示实体最近一次修改的 UTC 毫秒数。
        /// 每次更新实体时应同步刷新此值。
        /// </summary>
        public long UpdatedTimestamp { get; set; } = DateTimeOffset.Now.ToUnixTimeMilliseconds();
    }
}
