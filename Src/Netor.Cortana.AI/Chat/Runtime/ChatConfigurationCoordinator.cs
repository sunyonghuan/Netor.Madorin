using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.AI;

/// <summary>
/// 统一处理专家模式聊天的默认配置加载、启动日志与配置变更订阅。
/// </summary>
public sealed class ChatConfigurationCoordinator(
    ChatAgentResolver agentResolver,
    ChatSessionService sessionService,
    ISubscriber subscriber,
    ILogger<ChatConfigurationCoordinator> logger)
{
    private bool _eventsSubscribed;

    public void InitializeDefaults()
    {
        agentResolver.LoadDefaults();

        if (agentResolver.CurrentProvider is not null
            && agentResolver.CurrentAgent is not null
            && agentResolver.CurrentModel is not null)
        {
            logger.LogInformation(
                "AI 对话服务已加载默认配置：Provider={Provider}, Agent={Agent}, Model={Model}。智能体将在首次对话时构建。",
                agentResolver.CurrentProvider.Name,
                agentResolver.CurrentAgent.Name,
                agentResolver.CurrentModel);
            return;
        }

        logger.LogWarning("未找到默认的 AI 提供商/智能体/模型，AI 对话功能不可用");
    }

    public void SubscribeConfigChangeEvents()
    {
        if (_eventsSubscribed)
        {
            return;
        }

        _eventsSubscribed = true;

        subscriber.Subscribe<DataChangeArgs>(Events.OnAiProviderChange, (_, _) =>
        {
            ResetInitialization("AI 厂商");
            return Task.FromResult(false);
        });

        subscriber.Subscribe<DataChangeArgs>(Events.OnAiModelChange, (_, _) =>
        {
            ResetInitialization("AI 模型");
            return Task.FromResult(false);
        });

        subscriber.Subscribe<DataChangeArgs>(Events.OnAgentChange, (_, _) =>
        {
            ResetInitialization("智能体");
            return Task.FromResult(false);
        });

        subscriber.Subscribe<WorkspaceChangedArgs>(Events.OnWorkspaceChanged, (_, _) =>
        {
            ResetInitialization("工作目录");
            return Task.FromResult(false);
        });

        subscriber.Subscribe<VoiceSignalArgs>(Events.OnPluginsChanged, (_, _) =>
        {
            var version = agentResolver.BumpToolContextVersion("插件/MCP 工具");
            logger.LogInformation("插件/MCP 工具已变更，工具上下文版本推进到 {Version}", version);
            return Task.FromResult(false);
        });
    }

    private void ResetInitialization(string source)
    {
        agentResolver.LoadDefaults();
        sessionService.ClearCurrentSession();
        logger.LogInformation("{Source}配置已变更，AI 对话服务已重新加载默认配置", source);
    }
}
