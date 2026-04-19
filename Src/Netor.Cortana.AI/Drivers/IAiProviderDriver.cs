using Microsoft.Extensions.AI;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI.Drivers;

/// <summary>
/// 厂商协议驱动，负责聊天客户端创建、参数映射和模型发现。
/// </summary>
public interface IAiProviderDriver
{
    AiProviderDriverDefinition Definition { get; }

    bool CanHandle(AiProviderEntity provider);

    IChatClient CreateChatClient(AiProviderEntity provider, AiModelEntity model);

    ChatOptions BuildChatOptions(AiProviderEntity provider, AgentEntity agent);

    Task<IReadOnlyList<RemoteModelDescriptor>> FetchModelsAsync(
        AiProviderEntity provider,
        CancellationToken cancellationToken);
}