using System.Runtime.InteropServices;

using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.AI;

namespace Netor.Cortana.Pages;

/// <summary>
/// 暴露给 JavaScript 的设置桥接对象，提供数据 CRUD 操作。
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public class SettingsBridgeHostObject
{
    private AIAgentFactory AgentFactory => App.Services.GetRequiredService<AIAgentFactory>();
    private AiProviderService ProviderService => App.Services.GetRequiredService<AiProviderService>();
    private AiModelService ModelService => App.Services.GetRequiredService<AiModelService>();
    private AgentService AgentService => App.Services.GetRequiredService<AgentService>();
    private McpServerService McpServerService => App.Services.GetRequiredService<McpServerService>();
    private IPublisher Publisher => App.Services.GetRequiredService<IPublisher>();

    // ──────── AI 厂商 ────────

    /// <summary>
    /// 获取所有已启用的 AI 厂商列表（JSON）。
    /// </summary>
    public string GetProviders()
    {
        var list = ProviderService.GetAll();
        return JsonSerializer.Serialize(list);
    }

    /// <summary>
    /// 获取所有可选厂商驱动定义（JSON）。
    /// </summary>
    public string GetProviderDriverDefinitions()
    {
        return JsonSerializer.Serialize(AgentFactory.GetDriverDefinitions());
    }

    /// <summary>
    /// 保存（新增或更新）AI 厂商。
    /// </summary>
    public string SaveProvider(string json)
    {
        var entity = JsonSerializer.Deserialize<AiProviderEntity>(json);
        if (entity is null) return Error("无效的数据");
        if (!AgentFactory.IsDriverRegistered(entity.ProviderType)) return Error($"不支持的厂商类型：{entity.ProviderType}");

        var svc = ProviderService;
        var existing = svc.GetById(entity.Id);
        if (existing is null)
            svc.Add(entity);
        else
            svc.Update(entity);

        if (entity.IsDefault)
            svc.SetDefault(entity.Id);

        Publisher.Publish(Events.OnAiProviderChange, new DataChangeArgs(entity.Id, existing is null ? ChangeType.Create : ChangeType.Update));
        return Ok();
    }

    /// <summary>
    /// 删除指定 AI 厂商及其下属模型。
    /// </summary>
    public string DeleteProvider(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Error("ID 不能为空");

        ModelService.DeleteByProviderId(id);
        ProviderService.Delete(id);
        Publisher.Publish(Events.OnAiProviderChange, new DataChangeArgs(id, ChangeType.Delete));
        return Ok();
    }

    // ──────── AI 模型 ────────

    /// <summary>
    /// 获取指定厂商下的模型列表（JSON）。
    /// </summary>
    public string GetModels(string providerId)
    {
        var list = ModelService.GetByProviderId(providerId);
        return JsonSerializer.Serialize(list);
    }

    /// <summary>
    /// 保存（新增或更新）AI 模型。
    /// </summary>
    public string SaveModel(string json)
    {
        var entity = JsonSerializer.Deserialize<AiModelEntity>(json);
        if (entity is null) return Error("无效的数据");

        var svc = ModelService;
        var existing = svc.GetById(entity.Id);
        if (existing is null)
            svc.Add(entity);
        else
            svc.Update(entity);

        if (entity.IsDefault)
            svc.SetDefault(entity.Id);

        Publisher.Publish(Events.OnAiModelChange, new DataChangeArgs(entity.Id, existing is null ? ChangeType.Create : ChangeType.Update));
        return Ok();
    }

    /// <summary>
    /// 删除指定模型。
    /// </summary>
    public string DeleteModel(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Error("ID 不能为空");

        ModelService.Delete(id);
        Publisher.Publish(Events.OnAiModelChange, new DataChangeArgs(id, ChangeType.Delete));
        return Ok();
    }

    // ──────── 智能体 ────────

    /// <summary>
    /// 获取所有智能体列表（JSON）。
    /// </summary>
    public string GetAgents()
    {
        var list = AgentService.GetAll();
        return JsonSerializer.Serialize(list);
    }

    /// <summary>
    /// 保存（新增或更新）智能体。
    /// </summary>
    public string SaveAgent(string json)
    {
        var entity = JsonSerializer.Deserialize<AgentEntity>(json);
        if (entity is null) return Error("无效的数据");

        var svc = AgentService;
        var existing = svc.GetById(entity.Id);
        if (existing is null)
            svc.Add(entity);
        else
            svc.Update(entity);

        if (entity.IsDefault)
            svc.SetDefault(entity.Id);

        Publisher.Publish(Events.OnAgentChange, new DataChangeArgs(entity.Id, existing is null ? ChangeType.Create : ChangeType.Update));
        return Ok();
    }

    /// <summary>
    /// 删除指定智能体。
    /// </summary>
    public string DeleteAgent(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Error("ID 不能为空");

        AgentService.Delete(id);
        Publisher.Publish(Events.OnAgentChange, new DataChangeArgs(id, ChangeType.Delete));
        return Ok();
    }

    // ──────── 插件工具管理 ────────

    private PluginLoader PluginLoader => App.Services.GetRequiredService<PluginLoader>();

    /// <summary>
    /// 获取所有已加载插件的摘要信息（JSON），供前端工具管理面板展示。
    /// 返回结构：[{ id, name, description, tags, tools: [{ name, description }] }]
    /// </summary>
    public string GetAvailablePlugins()
    {
        var plugins = PluginLoader.GetActivePlugins();

        var result = plugins.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            description = p.Description,
            tags = p.Tags,
            tools = p.Tools.Select(t => new
            {
                name = t.Name,
                description = t.Description
            })
        });

        return JsonSerializer.Serialize(result);
    }

    /// <summary>
    /// 获取指定智能体已启用的插件 ID 列表（JSON）。
    /// </summary>
    public string GetAgentEnabledPlugins(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return "[]";

        var agent = AgentService.GetById(agentId);
        return JsonSerializer.Serialize(agent?.EnabledPluginIds ?? []);
    }

    /// <summary>
    /// 保存指定智能体的插件启用配置。
    /// </summary>
    public string SaveAgentPlugins(string agentId, string enabledPluginIdsJson)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return Error("智能体 ID 不能为空");

        var agent = AgentService.GetById(agentId);
        if (agent is null) return Error("智能体不存在");

        var pluginIds = JsonSerializer.Deserialize<List<string>>(enabledPluginIdsJson) ?? [];
        agent.EnabledPluginIds = pluginIds;
        AgentService.Update(agent);

        Publisher.Publish(Events.OnAgentChange, new DataChangeArgs(agentId, ChangeType.Update));
        return Ok();
    }

    // ──────── MCP 服务管理 ────────

    /// <summary>
    /// 获取所有 MCP 服务器配置列表（JSON）。
    /// </summary>
    public string GetMcpServers()
    {
        var list = McpServerService.GetAll();
        return JsonSerializer.Serialize(list);
    }

    /// <summary>
    /// 保存（新增或更新）MCP 服务器配置。
    /// </summary>
    public string SaveMcpServer(string json)
    {
        var entity = JsonSerializer.Deserialize<McpServerEntity>(json);
        if (entity is null) return Error("无效的数据");

        var svc = McpServerService;
        var existing = svc.GetById(entity.Id);
        if (existing is null)
            svc.Add(entity);
        else
            svc.Update(entity);

        // 如果启用，尝试重新连接
        var pluginLoader = PluginLoader;
        if (entity.IsEnabled)
        {
            _ = Task.Run(async () =>
            {
                try { await pluginLoader.AddMcpServerAsync(entity); }
                catch { /* 连接失败已在内部记录日志 */ }
            });
        }
        else
        {
            _ = Task.Run(async () =>
            {
                await pluginLoader.RemoveMcpServerAsync(entity.Id);
            });
        }

        return Ok();
    }

    /// <summary>
    /// 删除指定 MCP 服务器配置。
    /// </summary>
    public string DeleteMcpServer(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Error("ID 不能为空");

        McpServerService.Delete(id);

        // 断开连接
        var pluginLoader = PluginLoader;
        _ = Task.Run(async () =>
        {
            await pluginLoader.RemoveMcpServerAsync(id);
        });

        return Ok();
    }

    /// <summary>
    /// 测试 MCP 服务器连接。
    /// </summary>
    public string TestMcpConnection(string json)
    {
        var entity = JsonSerializer.Deserialize<McpServerEntity>(json);
        if (entity is null) return Error("无效的数据");

        McpServerHost? host = null;
        try
        {
            var loggerFactory = App.Services.GetRequiredService<ILoggerFactory>();
            host = new McpServerHost(entity, loggerFactory);
            host.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            var toolCount = host.Tools.Count;

            return JsonSerializer.Serialize(new { success = true, toolCount });
        }
        catch (Exception ex)
        {
            return Error($"连接失败：{ex.Message}");
        }
        finally
        {
            if (host is not null)
            {
                host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    /// <summary>
    /// 获取指定智能体已启用的 MCP 服务器 ID 列表（JSON）。
    /// </summary>
    public string GetAgentEnabledMcpServers(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return "[]";

        var agent = AgentService.GetById(agentId);
        return JsonSerializer.Serialize(agent?.EnabledMcpServerIds ?? []);
    }

    /// <summary>
    /// 保存指定智能体的 MCP 服务器启用配置。
    /// </summary>
    public string SaveAgentMcpServers(string agentId, string enabledMcpServerIdsJson)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return Error("智能体 ID 不能为空");

        var agent = AgentService.GetById(agentId);
        if (agent is null) return Error("智能体不存在");

        var mcpIds = JsonSerializer.Deserialize<List<string>>(enabledMcpServerIdsJson) ?? [];
        agent.EnabledMcpServerIds = mcpIds;
        AgentService.Update(agent);

        Publisher.Publish(Events.OnAgentChange, new DataChangeArgs(agentId, ChangeType.Update));
        return Ok();
    }

    /// <summary>
    /// 获取所有已连接的 MCP 服务器的工具摘要（JSON），供工具管理面板展示。
    /// 返回结构：[{ id, name, description, type: "mcp", tools: [{ name, description }] }]
    /// </summary>
    public string GetAvailableMcpTools()
    {
        var hosts = PluginLoader.GetActiveMcpServers();

        var result = hosts.Select(h => new
        {
            id = h.Id,
            name = h.Name,
            description = h.Description,
            type = "mcp",
            tools = h.Tools.Select(t => new
            {
                name = t.Name,
                description = t.Description
            })
        });

        return JsonSerializer.Serialize(result);
    }

    // ──────── 系统设置 ────────

    private SystemSettingsService SystemSettingsService => App.Services.GetRequiredService<SystemSettingsService>();

    /// <summary>
    /// 获取所有系统设置，按分组和排序权重排列，返回 JSON。
    /// </summary>
    public string GetSystemSettings()
    {
        var list = SystemSettingsService.GetAll();
        return JsonSerializer.Serialize(list);
    }

    /// <summary>
    /// 获取指定分组的系统设置，返回 JSON。
    /// </summary>
    public string GetSystemSettingsByGroup(string group)
    {
        if (string.IsNullOrWhiteSpace(group)) return "[]";
        var list = SystemSettingsService.GetByGroup(group);
        return JsonSerializer.Serialize(list);
    }

    /// <summary>
    /// 批量保存系统设置。接收 JSON 数组 [{ "Id": "键名", "Value": "值" }, ...]
    /// </summary>
    public string SaveSystemSettings(string json)
    {
        try
        {
            var items = JsonSerializer.Deserialize<List<SystemSettingUpdateDto>>(json);
            if (items is null) return Error("无效的数据");

            var updates = items
                .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                .Select(x => (x.Id!, x.Value ?? string.Empty));

            SystemSettingsService.SaveBatch(updates);

            // 工作目录变更时同步更新运行时状态
            var workspaceItem = items.FirstOrDefault(x => x.Id == "System.WorkspaceDirectory");
            if (workspaceItem is not null && !string.IsNullOrWhiteSpace(workspaceItem.Value)
                && Directory.Exists(workspaceItem.Value))
            {
                App.ChangeWorkspaceDirectory(workspaceItem.Value);
            }

            return Ok();
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>
    /// 将所有系统设置重置为默认值。
    /// </summary>
    public string ResetSystemSettings()
    {
        SystemSettingsService.ResetAllToDefault();
        return Ok();
    }

    /// <summary>
    /// 打开文件夹选择对话框，让用户选择工作目录。
    /// 返回 JSON { success: true, path: "..." } 或 { success: false }。
    /// </summary>
    public string SelectWorkspaceDirectory()
    {
        var result = string.Empty;
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        System.Windows.Forms.Application.OpenForms[0]?.Invoke(() =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "选择工作目录",
                UseDescriptionForTitle = true,
                SelectedPath = App.WorkspaceDirectory,
                ShowNewFolderButton = true
            };

            if (dialog.ShowDialog() == DialogResult.OK)
                tcs.SetResult(dialog.SelectedPath);
            else
                tcs.SetResult(string.Empty);
        });

        result = tcs.Task.GetAwaiter().GetResult();

        if (string.IsNullOrWhiteSpace(result))
            return JsonSerializer.Serialize(new { success = false });

        return JsonSerializer.Serialize(new { success = true, path = result });
    }

    // DTO for SaveSystemSettings
    private sealed class SystemSettingUpdateDto
    {
        public string? Id { get; set; }
        public string? Value { get; set; }
    }

    // ──────── 辅助方法 ────────

    private static string Ok() => JsonSerializer.Serialize(new { success = true });

    private static string Error(string message) => JsonSerializer.Serialize(new { success = false, error = message });
}
