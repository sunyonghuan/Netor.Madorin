using Anthropic;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Providers;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.Plugin;

using OllamaSharp;

using OpenAI;

using System.ClientModel;
using System.Net;

namespace Netor.Cortana.AI;

/// <summary>
/// 智能体工厂，根据提供商和智能体配置创建 <see cref="AIAgent"/> 实例。
/// 支持 Ollama（本地网络）和 OpenAI 兼容协议两种模式。
/// </summary>
public sealed class AIAgentFactory(
    IAppPaths appPaths,
    IEnumerable<AIContextProvider> builtInProviders,
    PluginLoader pluginLoader,
    IServiceProvider services,
    SystemSettingsService systemSettings,
    ILogger<AIAgentFactory> logger)
{
    public TokenTrackingChatClient? ChatClient { get; private set; }

    /// <summary>
    /// 根据提供商配置创建 <see cref="IChatClient"/>。
    /// 本地网络使用 OllamaApiClient，其余使用 OpenAI 兼容协议。
    /// </summary>
    public static IChatClient CreateChatClient(AiProviderEntity provider, string modelName)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        if (string.Equals(provider.ProviderType, "Ollama", StringComparison.OrdinalIgnoreCase))
        {
            return new OllamaApiClient(provider.Url, modelName);
        }
        if (string.Equals(provider.ProviderType, "Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return new AnthropicClient(new Anthropic.Core.ClientOptions()
            {
                ApiKey = provider.Key,
                BaseUrl = provider.Url,
                AuthToken = provider.AuthToken,
                Timeout = TimeSpan.FromMinutes(10)
            })
                .AsIChatClient(modelName);
        }
        var credential = new ApiKeyCredential(provider.Key);
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(provider.Url.TrimEnd('/')),
            NetworkTimeout = TimeSpan.FromMinutes(10)
        };

        return new OpenAIClient(credential, options)
            .GetChatClient(modelName)
            .AsIChatClient();
    }

    /// <summary>
    /// 根据智能体参数构建 <see cref="ChatOptions"/>。
    /// </summary>
    public static ChatOptions BuildOptions(AgentEntity agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

#pragma warning disable MEAI001
        var options = new ChatOptions
        {
            Temperature = (float)agent.Temperature,
            TopP = (float)agent.TopP,
            FrequencyPenalty = (float)agent.FrequencyPenalty,
            PresencePenalty = (float)agent.PresencePenalty,
            Instructions = agent.Instructions,
            AllowBackgroundResponses = false,
            Tools = [],
            AdditionalProperties = new AdditionalPropertiesDictionary(new Dictionary<string, object?>()
            {
                // 尝试启用流式 usage
                ["stream_options"] = new { include_usage = true }
            })
        };
#pragma warning restore MEAI001

        if (agent.MaxTokens > 0)
        {
            options.MaxOutputTokens = agent.MaxTokens;
        }

        return options;
    }

    /// <summary>
    /// 构建 <see cref="AIAgent"/>，组装内置提供器、插件提供器和历史记录提供器。
    /// 技能目录：应用启动目录/skills + 工作区/.cortana/skills。
    /// </summary>
    public AIAgent Build(AgentEntity agent, AiProviderEntity provider, AiModelEntity? model)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);

        var skillDirs = new List<string>
        {
            appPaths.UserSkillsDirectory,
            appPaths.WorkspaceSkillsDirectory
        };

        // 内置 Provider（通过 DI 批量注入）
#pragma warning disable MAAI001
        var providers = new List<AIContextProvider>
        {
            new AgentSkillsProvider(skillDirs)
        };
#pragma warning restore MAAI001

        providers.AddRange(builtInProviders);

        // 工具名称唯一性检查：key=工具名，value=来源描述
        var registeredTools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 合并该智能体已启用的插件 Provider
        var enabledIds = agent.EnabledPluginIds;

        foreach (var plugin in pluginLoader.GetActivePlugins())
        {
            if (!enabledIds.Contains(plugin.Id)) continue;

            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var tool in plugin.Tools)
            {
                var toolName = tool.Name;

                if (toolName.StartsWith("sys_", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(
                        "工具名称冲突，已跳过：工具 '{ToolName}' 使用了系统保留前缀 'sys_'，来源：插件 [{PluginName}]({PluginId} v{Version})",
                        toolName, plugin.Name, plugin.Id, plugin.Version);
                    excluded.Add(toolName);
                    continue;
                }

                if (registeredTools.TryGetValue(toolName, out var existingSource))
                {
                    logger.LogWarning(
                        "工具名称冲突，已跳过：工具 '{ToolName}' 来自插件 [{PluginName}]({PluginId} v{Version}) 与已注册的 [{ExistingSource}] 重复",
                        toolName, plugin.Name, plugin.Id, plugin.Version, existingSource);
                    excluded.Add(toolName);
                }
                else
                {
                    registeredTools[toolName] = $"插件 {plugin.Name}({plugin.Id})";
                }
            }

            providers.Add(excluded.Count > 0
                ? new PluginContextProvider(plugin, excluded)
                : new PluginContextProvider(plugin));
        }

        // 合并该智能体已启用的 MCP Server Provider
        var enabledMcpIds = agent.EnabledMcpServerIds;

        foreach (var mcpHost in pluginLoader.GetActiveMcpServers())
        {
            if (!enabledMcpIds.Contains(mcpHost.Id)) continue;

            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var tool in mcpHost.Tools)
            {
                var toolName = tool.Name;

                if (toolName.StartsWith("sys_", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(
                        "工具名称冲突，已跳过：工具 '{ToolName}' 使用了系统保留前缀 'sys_'，来源：MCP 服务器 [{McpName}]({McpId})",
                        toolName, mcpHost.Name, mcpHost.Id);
                    excluded.Add(toolName);
                    continue;
                }

                if (registeredTools.TryGetValue(toolName, out var existingSource))
                {
                    logger.LogWarning(
                        "工具名称冲突，已跳过：工具 '{ToolName}' 来自 MCP 服务器 [{McpName}]({McpId}) 与已注册的 [{ExistingSource}] 重复",
                        toolName, mcpHost.Name, mcpHost.Id, existingSource);
                    excluded.Add(toolName);
                }
                else
                {
                    registeredTools[toolName] = $"MCP {mcpHost.Name}({mcpHost.Id})";
                }
            }

            providers.Add(excluded.Count > 0
                ? new McpContextProvider(mcpHost, excluded)
                : new McpContextProvider(mcpHost));
        }
        ChatClient = new TokenTrackingChatClient(CreateChatClient(provider, model?.Name ?? string.Empty), model?.ContextLength ?? 128000);
#pragma warning disable MAAI001 // 类型仅用于评估，在将来的更新中可能会被更改或删除。取消此诊断以继续。
        return ChatClient
            .AsBuilder()
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Name = agent.Name,
                AIContextProviders = providers,
                ChatOptions = BuildOptions(agent),
                ChatHistoryProvider = services.GetRequiredService<ChatHistoryDataProvider>(),
            })
            .AsBuilder()
            .Build();
#pragma warning restore MAAI001 // 类型仅用于评估，在将来的更新中可能会被更改或删除。取消此诊断以继续。
    }

    /// <summary>
    /// 判断 URI 是否指向本地网络地址。
    /// </summary>
    private static bool IsLocalNetwork(Uri uri)
    {
        var host = uri.Host;

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!IPAddress.TryParse(host, out var ip))
            return false;

        byte[] bytes = ip.GetAddressBytes();
        if (bytes.Length != 4)
            return false;

        return bytes[0] == 10                                          // 10.0.0.0/8
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)   // 172.16.0.0/12
            || (bytes[0] == 192 && bytes[1] == 168)                    // 192.168.0.0/16
            || bytes[0] == 127;                                        // 127.0.0.0/8
    }
}