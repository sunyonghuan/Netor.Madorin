using Microsoft.Extensions.DependencyInjection;

namespace Cortana.Plugins.Memory.Storage;

/// <summary>
/// 记忆存储系统的依赖注入注册扩展。
/// </summary>
public static class MemoryStorageServiceCollectionExtensions
{
    /// <summary>
    /// 注册记忆存储门面及其内部数据库实现。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>注册完成后的服务集合。</returns>
    public static IServiceCollection AddMemoryStorage(this IServiceCollection services)
    {
        services.AddSingleton<IMemoryDatabase, SqliteMemoryDatabase>();
        services.AddSingleton<ObservationRecordsTable>();
        services.AddSingleton<MemoryFragmentsTable>();
        services.AddSingleton<MemoryAbstractionsTable>();
        services.AddSingleton<MemoryLinksTable>();
        services.AddSingleton<MemoryEventsTable>();
        services.AddSingleton<RecallLogsTable>();
        services.AddSingleton<MemoryMutationsTable>();
        services.AddSingleton<MemorySettingsTable>();
        services.AddSingleton<MemoryProcessingStatesTable>();
        services.AddSingleton<IMemoryStore, MemoryStore>();

        return services;
    }
}
