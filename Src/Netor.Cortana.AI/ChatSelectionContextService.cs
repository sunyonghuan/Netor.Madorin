using Microsoft.Extensions.AI;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI;

/// <summary>
/// 提供当前默认选择快照、handoff runtime context 与输出能力校验。
/// </summary>
public sealed class ChatSelectionContextService(
    ChatAgentResolver agentResolver,
    ChatSessionService sessionService,
    IAppPaths appPaths)
{
    public string? CurrentWorkspaceId => appPaths.WorkspaceDirectory.Md5Encrypt();

    public AiProviderEntity? CurrentProvider => agentResolver.CurrentProvider;

    public AgentEntity? CurrentAgent => agentResolver.CurrentAgent;

    public AiModelEntity? CurrentModel => agentResolver.CurrentModel;

    public bool TryGetCurrent(out ChatSelectionContext selectionContext)
    {
        if (CurrentProvider is null || CurrentAgent is null || CurrentModel is null)
        {
            selectionContext = null!;
            return false;
        }

        selectionContext = new ChatSelectionContext(
            CurrentProvider,
            CurrentAgent,
            CurrentModel,
            sessionService.CurrentId,
            CurrentWorkspaceId);
        return true;
    }

    public void EnsureOutputCapability(ChatSelectionContext selectionContext, OutputCapabilities capability)
    {
        if (selectionContext.Model.OutputCapabilities.HasFlag(capability))
        {
            return;
        }

        throw new InvalidOperationException(GetOutputCapabilityErrorMessage(capability));
    }

    private static string GetOutputCapabilityErrorMessage(OutputCapabilities capability)
    {
        return capability switch
        {
            OutputCapabilities.Image => "当前模型未启用图片输出能力，请先在模型设置中勾选 OutputCapabilities.Image。",
            OutputCapabilities.Video => "当前模型未启用视频输出能力，请先在模型设置中勾选 OutputCapabilities.Video。",
            _ => $"当前模型未启用 {capability} 输出能力。"
        };
    }
}
