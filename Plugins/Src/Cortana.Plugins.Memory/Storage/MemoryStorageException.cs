namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// 记忆存储层操作失败时抛出的统一异常。
/// </summary>
/// <remarks>
/// 该异常用于隔离业务层与具体数据库实现，业务服务只需要感知存储操作失败，
/// 不需要直接依赖 SQLite 或其他底层数据库异常类型。
/// </remarks>
public sealed class MemoryStorageException : InvalidOperationException
{
    /// <summary>
    /// 使用错误消息和底层异常创建记忆存储异常。
    /// </summary>
    /// <param name="message">面向存储抽象的错误消息。</param>
    /// <param name="innerException">导致存储操作失败的底层异常。</param>
    public MemoryStorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
