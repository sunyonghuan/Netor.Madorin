using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

using Netor.Cortana.AI.Drivers;

namespace Netor.Cortana.AI;

/// <summary>
/// 通过厂商驱动拉取远程模型列表，
/// 并转换为 <see cref="AiModelEntity"/> 持久化到本地数据库。
/// </summary>
/// <param name="modelService">AI 模型数据服务</param>
public sealed class AiModelFetcherService(AiProviderDriverRegistry driverRegistry, AiModelService modelService)
{
    private readonly AiProviderDriverRegistry _driverRegistry = driverRegistry ?? throw new ArgumentNullException(nameof(driverRegistry));
    private readonly AiModelService _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));

    /// <summary>
    /// 从远程 API 拉取模型列表并写入数据库。
    /// </summary>
    /// <param name="provider">AI 服务提供商实体（需包含 Url 和 Key）</param>
    /// <returns>写入的模型实体列表</returns>
    public async Task<List<AiModelEntity>> FetchAndSaveModelsAsync(
        AiProviderEntity provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var driver = _driverRegistry.Resolve(provider);
        var models = await driver.FetchModelsAsync(provider, cancellationToken);

        var entities = new List<AiModelEntity>();
        foreach (var model in models)
        {
            entities.Add(new AiModelEntity
            {
                Name = model.Name,
                DisplayName = (string.IsNullOrWhiteSpace(model.DisplayName) ? model.Name : model.DisplayName) ?? "没有名称",
                Description = model.Description ?? string.Empty,
                ModelType = string.IsNullOrWhiteSpace(model.ModelType) ? "chat" : model.ModelType,
                ContextLength = model.ContextLength ?? 0,
                IsEnabled = true,
                ProviderId = provider.Id,
                // 默认开启函数调用能力，便于工具链直接可用
                InteractionCapabilities = InteractionCapabilities.FunctionCall,
            });
        }

        if (entities.Count > 0)
        {
            // 先清除旧数据再写入
            _modelService.DeleteByProviderId(provider.Id);
            _modelService.BatchInsert(entities);
        }

        return entities;
    }
}
