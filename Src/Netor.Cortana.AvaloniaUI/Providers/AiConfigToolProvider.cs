using System.Text;

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

    // ──────── 指令 ────────

    private static string BuildInstructions() => """
        ### AI 配置管理工具使用规范

        **术语说明：** 用户提到「智能体」「助理」「代理」「Agent」时，均指同一概念。

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

        #### 智能体提示词
        - 用户说"看看你的提示词""你的 prompt 是什么"等，调用 sys_get_agent_instructions(0) 获取默认智能体的提示词
        - 用户指定了序号或名称，先 sys_list_agents 确定序号，再 sys_get_agent_instructions(序号)
        - 修改提示词：先 sys_list_agents → 用户选序号 → 用户提供新提示词 → sys_update_agent_instructions
        """;
}