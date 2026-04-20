using System.Text;

using Netor.Cortana.Plugin;

namespace Netor.Cortana.AvaloniaUI.Providers;

/// <summary>
/// AI 配置管理工具提供者，向 AI 提供厂商/模型/智能体的查询、切换与增改能力。
/// </summary>
internal sealed class AiConfigToolProvider(
    ILogger<AiConfigToolProvider> logger,
    IServiceProvider serviceProvider,
    IPublisher publisher) : AIContextProvider
{
    private readonly List<AITool> _tools = [];

    private AiProviderService ProviderService => serviceProvider.GetRequiredService<AiProviderService>();
    private AiModelService ModelService => serviceProvider.GetRequiredService<AiModelService>();
    private AgentService AgentService => serviceProvider.GetRequiredService<AgentService>();
    private McpServerService McpServerService => serviceProvider.GetRequiredService<McpServerService>();
    private PluginLoader PluginLoader => serviceProvider.GetRequiredService<PluginLoader>();
    private ILoggerFactory LoggerFactory => serviceProvider.GetRequiredService<ILoggerFactory>();

    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        if (_tools.Count == 0)
            RegisterTools();

        return ValueTask.FromResult(new AIContext
        {
            Instructions = BuildInstructions(),
            Tools = _tools
        });
    }

    // ──────── 工具注册 ────────

    private void RegisterTools()
    {
        // Query
        _tools.Add(AIFunctionFactory.Create(
            name: "sys_list_providers",
            description: "List all enabled AI service providers, returning a numbered list. Users can select by index.",
            method: ListProviders));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_list_agents",
            description: "List all enabled agents (assistants/proxies/Agents), returning a numbered list. Users can select by index.",
            method: ListAgents));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_list_models",
            description: "List all enabled models under the current default provider, returning a numbered list. Users can select by index.",
            method: ListModels));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_list_mcp_servers",
            description: "List all MCP server configurations, including enabled/disabled state, current connection state, and discovered tool count.",
            method: ListMcpServers));

        // Set Default
        _tools.Add(AIFunctionFactory.Create(
            name: "sys_set_default_provider",
            description: "Set the provider at the specified index as default. Call sys_list_providers first to get the list, then set based on the user's specified index. Parameter: index (1-based index).",
            method: (int index) => SetDefaultProvider(index)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_set_default_agent",
            description: "Set the agent at the specified index as default. Call sys_list_agents first to get the list, then set based on the user's specified index. Parameter: index (1-based index).",
            method: (int index) => SetDefaultAgent(index)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_set_default_model",
            description: "Set the model at the specified index as default. Call sys_list_models first to get the list, then set based on the user's specified index. Parameter: index (1-based index).",
            method: (int index) => SetDefaultModel(index)));

        // Add New
        _tools.Add(AIFunctionFactory.Create(
            name: "sys_add_provider",
            description: "Add a new AI service provider. Parameters: name (provider name), url (API endpoint), key (API key), providerType (provider type, default OpenAI).",
            method: (string name, string url, string key, string providerType) => AddProvider(name, url, key, providerType)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_add_model",
            description: "Add a new model under the specified provider. Parameters: providerIndex (provider index, 1-based, call sys_list_providers first to get), name (model name/ID), displayName (display name, can be empty).",
            method: (int providerIndex, string name, string displayName) => AddModel(providerIndex, name, displayName)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_add_agent",
            description: "Add a new agent (assistant/proxy/Agent). Parameters: name (name), instructions (system prompt).",
            method: (string name, string instructions) => AddAgent(name, instructions)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_test_mcp_server",
            description: "Test an MCP server configuration without saving it. Parameters: name, transportType (stdio/sse/streamable-http), command, arguments (comma or newline separated), url, apiKey, environmentVariables (newline separated KEY=VALUE), description.",
            method: (string name, string transportType, string command, string arguments, string url, string apiKey, string environmentVariables, string description, CancellationToken ct)
                => TestMcpServerAsync(name, transportType, command, arguments, url, apiKey, environmentVariables, description, ct)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_add_mcp_server",
            description: "Add a new MCP server. This tool tests the connection first, then saves to the database and connects it to the running system only if the test succeeds. Parameters: name, transportType (stdio/sse/streamable-http), command, arguments (comma or newline separated), url, apiKey, environmentVariables (newline separated KEY=VALUE), description.",
            method: (string name, string transportType, string command, string arguments, string url, string apiKey, string environmentVariables, string description, CancellationToken ct)
                => AddMcpServerAsync(name, transportType, command, arguments, url, apiKey, environmentVariables, description, ct)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_enable_mcp_for_agent",
            description: "Enable a saved MCP server for an agent. Parameters: mcpIndex (1-based index, call sys_list_mcp_servers first), agentIndex (1-based index, 0 means current default agent).",
            method: (int mcpIndex, int agentIndex) => EnableMcpForAgent(mcpIndex, agentIndex)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_disconnect_mcp_server",
            description: "Disconnect a specific MCP server by index. Parameter: mcpIndex (1-based, call sys_list_mcp_servers first). Pass 0 to disconnect ALL MCP servers.",
            method: (int mcpIndex, CancellationToken ct) => DisconnectMcpServerAsync(mcpIndex, ct)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_reconnect_mcp_server",
            description: "Reconnect a specific MCP server by index. Parameter: mcpIndex (1-based, call sys_list_mcp_servers first). Pass 0 to reconnect ALL enabled MCP servers.",
            method: (int mcpIndex, CancellationToken ct) => ReconnectMcpServerAsync(mcpIndex, ct)));

        // Agent Instructions
        _tools.Add(AIFunctionFactory.Create(
            name: "sys_get_agent_instructions",
            description: "Get the system prompt for an agent. Parameters: agentIndex (1-based index, 0 means get the current default agent's prompt). Pass 0 when user says \"show me your prompt\".",
            method: (int agentIndex) => GetAgentInstructions(agentIndex)));

        _tools.Add(AIFunctionFactory.Create(
            name: "sys_update_agent_instructions",
            description: "Update the system prompt for a specified agent. Parameters: agentIndex (1-based index, call sys_list_agents first to get), instructions (new prompt content).",
            method: (int agentIndex, string instructions) => UpdateAgentInstructions(agentIndex, instructions)));
    }

    // ──────── AI 配置查询 ────────

    private string ListProviders()
    {
        var list = ProviderService.GetAll();
        if (list.Count == 0)
            return "当前没有已启用的 AI 厂商。";

        var sb = new StringBuilder();
        sb.AppendLine("AI 厂商列表：");
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            var marker = p.IsDefault ? " ★默认" : "";
            sb.AppendLine($"  {i + 1}. {p.Name}{marker} [id={p.Id}]");
        }

        return sb.ToString();
    }

    private string ListAgents()
    {
        var list = AgentService.GetAll();
        if (list.Count == 0)
            return "当前没有已启用的智能体。";

        var sb = new StringBuilder();
        sb.AppendLine("智能体列表：");
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            var marker = a.IsDefault ? " ★默认" : "";
            sb.AppendLine($"  {i + 1}. {a.Name}{marker} [id={a.Id}]");
        }

        return sb.ToString();
    }

    private string ListModels()
    {
        var providers = ProviderService.GetAll();
        var defaultProvider = providers.FirstOrDefault(p => p.IsDefault) ?? providers.FirstOrDefault();
        if (defaultProvider is null)
            return "没有可用的 AI 厂商，无法获取模型列表。";

        var list = ModelService.GetByProviderId(defaultProvider.Id);
        if (list.Count == 0)
            return $"厂商「{defaultProvider.Name}」下没有已启用的模型。";

        var sb = new StringBuilder();
        sb.AppendLine($"厂商「{defaultProvider.Name}」的模型列表：");
        for (int i = 0; i < list.Count; i++)
        {
            var m = list[i];
            var displayName = string.IsNullOrWhiteSpace(m.DisplayName) ? m.Name : m.DisplayName;
            var marker = m.IsDefault ? " ★默认" : "";
            sb.AppendLine($"  {i + 1}. {displayName}{marker} [id={m.Id}]");
        }

        return sb.ToString();
    }

    private string ListMcpServers()
    {
        var list = McpServerService.GetAll();
        if (list.Count == 0)
            return "当前没有 MCP 服务配置。";

        var activeHosts = PluginLoader.GetActiveMcpServers()
            .ToDictionary(host => host.Id, host => host, StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine("MCP 服务列表：");

        for (int i = 0; i < list.Count; i++)
        {
            var server = list[i];
            var isConnected = activeHosts.TryGetValue(server.Id, out var host);
            var enabledText = server.IsEnabled ? "已启用" : "已禁用";
            var connectedText = isConnected ? "已连接" : "未连接";
            var toolCountText = isConnected ? $"{host!.Tools.Count} 个工具" : "0 个工具";
            var address = server.TransportType == "stdio"
                ? server.Command
                : server.Url;

            sb.AppendLine($"  {i + 1}. {server.Name} [{server.TransportType}] · {enabledText} · {connectedText} · {toolCountText} [id={server.Id}]");

            if (!string.IsNullOrWhiteSpace(address))
            {
                sb.AppendLine($"     {address}");
            }
        }

        return sb.ToString();
    }

    // ──────── 切换默认 ────────

    private string SetDefaultProvider(int index)
    {
        var list = ProviderService.GetAll();
        if (index < 1 || index > list.Count)
            return $"✗ 无效的序号 {index}，有效范围 1~{list.Count}。";

        var target = list[index - 1];
        ProviderService.SetDefault(target.Id);

        publisher.Publish(Events.OnAiProviderChange, new DataChangeArgs(target.Id, ChangeType.Update));
        logger.LogInformation("已将默认厂商设置为：{Name}", target.Name);

        return $"✓ 已将默认厂商设置为「{target.Name}」";
    }

    private string SetDefaultAgent(int index)
    {
        var list = AgentService.GetAll();
        if (index < 1 || index > list.Count)
            return $"✗ 无效的序号 {index}，有效范围 1~{list.Count}。";

        var target = list[index - 1];
        AgentService.SetDefault(target.Id);

        publisher.Publish(Events.OnAgentChange, new DataChangeArgs(target.Id, ChangeType.Update));
        logger.LogInformation("已将默认智能体设置为：{Name}", target.Name);

        return $"✓ 已将默认智能体设置为「{target.Name}」";
    }

    private string SetDefaultModel(int index)
    {
        var providers = ProviderService.GetAll();
        var defaultProvider = providers.FirstOrDefault(p => p.IsDefault) ?? providers.FirstOrDefault();
        if (defaultProvider is null)
            return "✗ 没有可用的 AI 厂商，无法设置默认模型。";

        var list = ModelService.GetByProviderId(defaultProvider.Id);
        if (index < 1 || index > list.Count)
            return $"✗ 无效的序号 {index}，有效范围 1~{list.Count}。";

        var target = list[index - 1];
        ModelService.SetDefault(target.Id);

        publisher.Publish(Events.OnAiModelChange, new DataChangeArgs(target.Id, ChangeType.Update));
        logger.LogInformation("已将默认模型设置为：{Name}", target.Name);

        var displayName = string.IsNullOrWhiteSpace(target.DisplayName) ? target.Name : target.DisplayName;

        return $"✓ 已将默认模型设置为「{displayName}」";
    }

    // ──────── 新增 ────────

    private string AddProvider(string name, string url, string key, string providerType)
    {
        try
        {
            var entity = new AiProviderEntity
            {
                Name = name,
                Url = url,
                Key = key,
                ProviderType = string.IsNullOrWhiteSpace(providerType) ? "OpenAI" : providerType,
                IsEnabled = true
            };

            ProviderService.Add(entity);
            publisher.Publish(Events.OnAiProviderChange, new DataChangeArgs(entity.Id, ChangeType.Create));
            logger.LogInformation("已新增厂商：{Name}", name);

            return $"✓ 已新增厂商「{name}」（类型：{entity.ProviderType}）";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "新增厂商失败");
            return $"✗ 新增厂商失败：{ex.Message}";
        }
    }

    private string AddModel(int providerIndex, string name, string displayName)
    {
        try
        {
            var providers = ProviderService.GetAll();
            if (providerIndex < 1 || providerIndex > providers.Count)
                return $"✗ 无效的厂商序号 {providerIndex}，有效范围 1~{providers.Count}。请先调用 sys_list_providers 查看列表。";

            var provider = providers[providerIndex - 1];
            var entity = new AiModelEntity
            {
                Name = name,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName,
                ProviderId = provider.Id,
                IsEnabled = true
            };

            ModelService.Add(entity);
            publisher.Publish(Events.OnAiModelChange, new DataChangeArgs(entity.Id, ChangeType.Create));
            logger.LogInformation("已在厂商 {Provider} 下新增模型：{Name}", provider.Name, name);

            return $"✓ 已在厂商「{provider.Name}」下新增模型「{entity.DisplayName}」";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "新增模型失败");
            return $"✗ 新增模型失败：{ex.Message}";
        }
    }

    private string AddAgent(string name, string instructions)
    {
        try
        {
            var entity = new AgentEntity
            {
                Name = name,
                Instructions = instructions ?? "",
                IsEnabled = true
            };

            AgentService.Add(entity);
            publisher.Publish(Events.OnAgentChange, new DataChangeArgs(entity.Id, ChangeType.Create));
            logger.LogInformation("已新增智能体：{Name}", name);

            return $"✓ 已新增智能体「{name}」";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "新增智能体失败");
            return $"✗ 新增智能体失败：{ex.Message}";
        }
    }

    // ──────── 智能体提示词 ────────

    private string GetAgentInstructions(int agentIndex)
    {
        try
        {
            var list = AgentService.GetAll();
            if (list.Count == 0)
                return "当前没有已启用的智能体。";

            AgentEntity target;
            if (agentIndex <= 0)
            {
                // 获取默认智能体的提示词
                target = list.FirstOrDefault(a => a.IsDefault) ?? list[0];
            }
            else
            {
                if (agentIndex > list.Count)
                    return $"✗ 无效的序号 {agentIndex}，有效范围 1~{list.Count}。";
                target = list[agentIndex - 1];
            }

            if (string.IsNullOrWhiteSpace(target.Instructions))
                return $"智能体「{target.Name}」的提示词为空。";

            return $"智能体「{target.Name}」的提示词：\n{target.Instructions}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "获取智能体提示词失败");
            return $"✗ 获取智能体提示词失败：{ex.Message}";
        }
    }

    private string UpdateAgentInstructions(int agentIndex, string instructions)
    {
        try
        {
            var list = AgentService.GetAll();
            if (agentIndex < 1 || agentIndex > list.Count)
                return $"✗ 无效的序号 {agentIndex}，有效范围 1~{list.Count}。请先调用 sys_list_agents 查看列表。";

            var target = list[agentIndex - 1];
            target.Instructions = instructions ?? "";
            AgentService.Update(target);

            publisher.Publish(Events.OnAgentChange, new DataChangeArgs(target.Id, ChangeType.Update));
            logger.LogInformation("已修改智能体 {Name} 的提示词", target.Name);

            return $"✓ 已修改智能体「{target.Name}」的提示词";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "修改智能体提示词失败");
            return $"✗ 修改智能体提示词失败：{ex.Message}";
        }
    }

    // ──────── MCP 服务 ────────

    private async Task<string> TestMcpServerAsync(
        string name,
        string transportType,
        string command,
        string arguments,
        string url,
        string apiKey,
        string environmentVariables,
        string description,
        CancellationToken cancellationToken)
    {
        try
        {
            var entity = BuildMcpEntity(name, transportType, command, arguments, url, apiKey, environmentVariables, description);
            var result = await TestMcpConnectionAsync(entity, cancellationToken);
            return result.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "测试 MCP 服务失败");
            return $"✗ 测试 MCP 服务失败：{ex.Message}";
        }
    }

    private async Task<string> AddMcpServerAsync(
        string name,
        string transportType,
        string command,
        string arguments,
        string url,
        string apiKey,
        string environmentVariables,
        string description,
        CancellationToken cancellationToken)
    {
        McpServerEntity entity;

        try
        {
            entity = BuildMcpEntity(name, transportType, command, arguments, url, apiKey, environmentVariables, description);
        }
        catch (Exception ex)
        {
            return $"✗ MCP 参数无效：{ex.Message}";
        }

        var testResult = await TestMcpConnectionAsync(entity, cancellationToken);
        if (!testResult.Success)
        {
            return testResult.Message + "\n未保存到数据库。";
        }

        try
        {
            McpServerService.Add(entity);
            await PluginLoader.AddMcpServerAsync(entity, cancellationToken);

            var activeHost = PluginLoader.GetActiveMcpServers()
                .FirstOrDefault(host => string.Equals(host.Id, entity.Id, StringComparison.OrdinalIgnoreCase));

            if (activeHost is null)
            {
                McpServerService.Delete(entity.Id);
                return $"✗ MCP 服务「{entity.Name}」测试成功，但接入运行时失败，已回滚保存。";
            }

            logger.LogInformation("已新增 MCP 服务：{Name}", entity.Name);
            return $"✓ 已新增 MCP 服务「{entity.Name}」，连接成功，发现 {activeHost.Tools.Count} 个工具。若需要给智能体使用，请继续调用 sys_enable_mcp_for_agent。";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "新增 MCP 服务失败");

            try
            {
                await PluginLoader.RemoveMcpServerAsync(entity.Id);
            }
            catch
            {
            }

            try
            {
                McpServerService.Delete(entity.Id);
            }
            catch
            {
            }

            return $"✗ 新增 MCP 服务失败：{ex.Message}";
        }
    }

    private string EnableMcpForAgent(int mcpIndex, int agentIndex)
    {
        try
        {
            var mcpList = McpServerService.GetAll();
            if (mcpIndex < 1 || mcpIndex > mcpList.Count)
                return $"✗ 无效的 MCP 序号 {mcpIndex}，有效范围 1~{mcpList.Count}。请先调用 sys_list_mcp_servers 查看列表。";

            var targetMcp = mcpList[mcpIndex - 1];
            var activeHost = PluginLoader.GetActiveMcpServers()
                .FirstOrDefault(host => string.Equals(host.Id, targetMcp.Id, StringComparison.OrdinalIgnoreCase));

            if (activeHost is null)
                return $"✗ MCP 服务「{targetMcp.Name}」当前未连接，无法为智能体启用。请先测试或重新连接。";

            var agents = AgentService.GetAll();
            if (agents.Count == 0)
                return "✗ 当前没有可用的智能体。";

            AgentEntity targetAgent;
            if (agentIndex <= 0)
            {
                targetAgent = agents.FirstOrDefault(agent => agent.IsDefault) ?? agents[0];
            }
            else
            {
                if (agentIndex > agents.Count)
                    return $"✗ 无效的智能体序号 {agentIndex}，有效范围 1~{agents.Count}。请先调用 sys_list_agents 查看列表。";

                targetAgent = agents[agentIndex - 1];
            }

            if (targetAgent.EnabledMcpServerIds.Contains(targetMcp.Id, StringComparer.OrdinalIgnoreCase))
                return $"✓ 智能体「{targetAgent.Name}」已启用 MCP 服务「{targetMcp.Name}」，无需重复设置。";

            targetAgent.EnabledMcpServerIds =
            [
                .. targetAgent.EnabledMcpServerIds,
                targetMcp.Id
            ];

            AgentService.Update(targetAgent);
            publisher.Publish(Events.OnAgentChange, new DataChangeArgs(targetAgent.Id, ChangeType.Update));

            logger.LogInformation("已为智能体 {AgentName} 启用 MCP 服务 {McpName}", targetAgent.Name, targetMcp.Name);
            return $"✓ 已为智能体「{targetAgent.Name}」启用 MCP 服务「{targetMcp.Name}」。";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "为智能体启用 MCP 服务失败");
            return $"✗ 为智能体启用 MCP 服务失败：{ex.Message}";
        }
    }

    private async Task<string> DisconnectMcpServerAsync(int mcpIndex, CancellationToken cancellationToken)
    {
        try
        {
            if (mcpIndex == 0)
            {
                await PluginLoader.DisconnectAllMcpAsync();
                return "✓ 已断开所有 MCP 服务连接。";
            }

            var list = McpServerService.GetAll();
            if (mcpIndex < 1 || mcpIndex > list.Count)
                return $"✗ 无效的序号 {mcpIndex}，有效范围 1~{list.Count}。请先调用 sys_list_mcp_servers 查看列表。";

            var target = list[mcpIndex - 1];
            await PluginLoader.RemoveMcpServerAsync(target.Id);
            return $"✓ 已断开 MCP 服务「{target.Name}」。";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "断开 MCP 服务失败");
            return $"✗ 断开 MCP 服务失败：{ex.Message}";
        }
    }

    private async Task<string> ReconnectMcpServerAsync(int mcpIndex, CancellationToken cancellationToken)
    {
        try
        {
            if (mcpIndex == 0)
            {
                await PluginLoader.ReconnectAllMcpAsync(McpServerService, cancellationToken);
                return "✓ 已重新连接所有已启用的 MCP 服务。";
            }

            var list = McpServerService.GetAll();
            if (mcpIndex < 1 || mcpIndex > list.Count)
                return $"✗ 无效的序号 {mcpIndex}，有效范围 1~{list.Count}。请先调用 sys_list_mcp_servers 查看列表。";

            var target = list[mcpIndex - 1];
            await PluginLoader.ReconnectMcpAsync(target.Id, McpServerService, cancellationToken);
            return $"✓ 已重新连接 MCP 服务「{target.Name}」。";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "重新连接 MCP 服务失败");
            return $"✗ 重新连接 MCP 服务失败：{ex.Message}";
        }
    }

    private async Task<(bool Success, string Message)> TestMcpConnectionAsync(McpServerEntity entity, CancellationToken cancellationToken)
    {
        try
        {
            await using var host = new McpServerHost(entity, LoggerFactory);
            await host.ConnectAsync(cancellationToken);
            return (true, $"✓ MCP 服务「{entity.Name}」连接成功，发现 {host.Tools.Count} 个工具。");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MCP 服务 {Name} 连接测试失败", entity.Name);
            return (false, $"✗ MCP 服务「{entity.Name}」无法连通：{ex.Message}");
        }
    }

    private static McpServerEntity BuildMcpEntity(
        string name,
        string transportType,
        string command,
        string arguments,
        string url,
        string apiKey,
        string environmentVariables,
        string description)
    {
        var normalizedName = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
            throw new InvalidOperationException("名称不能为空。");

        var transport = NormalizeTransportType(transportType);

        var entity = new McpServerEntity
        {
            Name = normalizedName,
            TransportType = transport,
            Description = description?.Trim() ?? string.Empty,
            IsEnabled = true,
        };

        if (transport == "stdio")
        {
            entity.Command = command?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(entity.Command))
                throw new InvalidOperationException("stdio 模式必须提供启动命令。");

            entity.Arguments = ParseList(arguments);
            entity.EnvironmentVariables = ParseEnvironmentVariables(environmentVariables);
            entity.Url = string.Empty;
            entity.ApiKey = string.Empty;
        }
        else
        {
            entity.Url = url?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(entity.Url))
                throw new InvalidOperationException("HTTP 模式必须提供 URL。");

            entity.ApiKey = apiKey?.Trim() ?? string.Empty;
            entity.Command = string.Empty;
            entity.Arguments = [];
            entity.EnvironmentVariables = [];
        }

        return entity;
    }

    private static string NormalizeTransportType(string transportType)
    {
        var transport = transportType?.Trim().ToLowerInvariant();
        return transport switch
        {
            null or "" or "stdio" => "stdio",
            "sse" => "sse",
            "http" or "streamable-http" or "streamablehttp" => "streamable-http",
            _ => throw new InvalidOperationException($"不支持的传输类型：{transportType}。支持 stdio、sse、streamable-http。")
        };
    }

    private static List<string> ParseList(string text)
    {
        return (text ?? string.Empty)
            .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static Dictionary<string, string?> ParseEnvironmentVariables(string text)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in (text ?? string.Empty)
                     .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = line.IndexOf('=');
            if (index <= 0)
                continue;

            var key = line[..index].Trim();
            var value = line[(index + 1)..].Trim();

            if (!string.IsNullOrWhiteSpace(key))
                dict[key] = value;
        }

        return dict;
    }

    // ──────── 指令 ────────

    private static string BuildInstructions() => """
        ### AI 配置管理工具使用规范

        这些工具用于已具备可用 AI 厂商和模型后的软件配置与操作管理。

        **术语说明：** 用户提到「智能体」「助理」「代理」「Agent」时，均指同一概念。
        **术语说明：** 用户提到「MCP」「MCP 服务」「MCP 服务器」「MCP 工具服务」时，均指同一概念。

        #### 序号交互模式
        当用户想要查看或切换厂商/智能体/模型时：
        1. 先调用对应的 sys_list_* 工具获取列表，向用户展示带序号的列表
        2. 等待用户说出序号
        3. 再调用 sys_set_default_* 传入序号完成切换

        - 不要猜测序号，必须先列出列表
        - 如果用户直接说了名称而非序号，先 sys_list 找到匹配项的序号，再 set
        - 切换厂商后，模型列表会随之变化，需要重新 sys_list_models
        - list 返回的 [id=xxx] 是内部标识，展示给用户时省略

        #### 新增操作
        - 新增厂商：逐步引导用户提供 name → url → key → providerType（默认 OpenAI）
        - 新增模型：先 sys_list_providers，用户选厂商序号，再引导提供 name、displayName
        - 新增智能体：引导用户提供 name → instructions（系统提示词）
        - 默认智能体用于软件配置与操作辅助；当用户忘记创建智能体时，可以引导其查看、切换或新增智能体

        #### 软件配置范围
        - 这些工具主要处理软件内部的配置、切换、查看和接入工作
        - 可以帮助用户管理厂商、模型、智能体、MCP 服务以及与智能体绑定的工具能力
        - 不要把外部服务本身的开通、授权、采购或部署问题伪装成软件内部操作

        #### MCP 配置
        - 用户说「配置 MCP」「添加 MCP 服务」「接入 MCP」「连接 MCP」时，进入 MCP 配置流程
        - 先根据 transportType 区分参数
        - stdio：name → command → arguments → environmentVariables → description
        - sse / streamable-http：name → url → apiKey → description
        - 优先调用 sys_test_mcp_server 测试临时配置是否可连通
        - 测试成功后，再调用 sys_add_mcp_server 写入数据库并接入当前运行环境
        - 如果测试失败，要明确告诉用户当前无法连通，并说明未保存
        - 用户说「测试 MCP」「检查 MCP 能不能连」时，只调用 sys_test_mcp_server，不要写入数据库
        - 用户说「列出 MCP」「看看有哪些 MCP」时，调用 sys_list_mcp_servers
        - 用户说「给当前智能体启用这个 MCP」「让小月用这个 MCP」时，先 sys_list_mcp_servers 确认序号，再调用 sys_enable_mcp_for_agent
        - MCP 配置属于软件增强能力，不替代 AI 厂商和模型本身的服务接入

        #### 智能体提示词
        - 用户说"看看你的提示词""你的 prompt 是什么"等，调用 sys_get_agent_instructions(0) 获取默认智能体的提示词
        - 用户指定了序号或名称，先 sys_list_agents 确定序号，再 sys_get_agent_instructions(序号)
        - 修改提示词：先 sys_list_agents → 用户选序号 → 用户提供新提示词 → sys_update_agent_instructions
        """;
}