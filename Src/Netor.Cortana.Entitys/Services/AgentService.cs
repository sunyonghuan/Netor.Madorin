namespace Netor.Cortana.Entitys.Services;

/// <summary>
/// Agent 兼容服务。对外保留旧 AgentEntity API，内部读写文件版 Agent。
/// </summary>
public sealed class AgentService(AgentFileService fileService, SystemSettingsService settingsService)
{
    private const string DefaultAgentSettingKey = "Agent.DefaultName";
    private static readonly HashSet<string> HiddenSystemAgentNames =
    [
        "general-manager",
        "project-lead",
    ];

    public List<AgentEntity> GetAll()
    {
        return fileService.GetAll()
            .Where(static record => record.Manifest.Enabled)
            .Select(ToEntity)
            .ToList();
    }

    public List<AgentEntity> GetSelectable()
    {
        return GetAll()
            .Where(static agent => IsSelectable(agent.Id))
            .ToList();
    }

    public string GetDefaultName() => settingsService.GetValue(DefaultAgentSettingKey, "default");

    public bool IsDefault(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        string.Equals(name, GetDefaultName(), StringComparison.Ordinal);

    public AgentEntity? GetDefault() => GetByName(GetDefaultName());

    public AgentEntity? GetDefaultOrFirst()
    {
        var defaultName = GetDefaultName();
        var records = fileService.GetAll()
            .Where(static record => record.Manifest.Enabled)
            .ToList();
        var selectableRecords = records
            .Where(static record => IsSelectable(record.Manifest.Name))
            .ToList();
        var record = selectableRecords.FirstOrDefault(item => string.Equals(item.Manifest.Name, defaultName, StringComparison.Ordinal))
            ?? selectableRecords.FirstOrDefault()
            ?? records.FirstOrDefault();
        return record is null ? null : ToEntity(record);
    }

    public AgentEntity? GetById(string id)
    {
        // 兼容旧调用层：文件版 Agent 的 Id 实际承载 manifest name。
        return GetByName(id);
    }

    public AgentEntity? GetByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var record = fileService.GetByName(name);
        return record is null ? null : ToEntity(record);
    }

    public AgentEntity? FindByNameOrDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return GetAll().FirstOrDefault(agent =>
            string.Equals(agent.Id, name, StringComparison.Ordinal) ||
            string.Equals(agent.Name, name, StringComparison.Ordinal));
    }

    public void Add(AgentEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var manifestName = LooksLikeGeneratedId(entity.Id) ? CreateAgentName(entity.Name) : entity.Id;
        SaveEntity(entity, manifestName, AgentManifestKinds.Agent);
    }

    public void Update(AgentEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var existing = GetByName(entity.Id);
        SaveEntity(entity, entity.Id, existing is null ? AgentManifestKinds.Agent : GetManifestKind(entity.Id));
    }

    public void Delete(string id)
    {
        fileService.SoftDelete(id);
    }

    public void SetDefault(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Id cannot be null or empty.", nameof(id));
        }

        if (fileService.GetByName(id) is null)
        {
            throw new InvalidOperationException($"Agent 不存在：{id}");
        }

        settingsService.SetValue(DefaultAgentSettingKey, id);
    }

    private void SaveEntity(AgentEntity entity, string manifestName, string kind)
    {
        var displayName = string.IsNullOrWhiteSpace(entity.Name) ? manifestName : entity.Name.Trim();
        var manifest = new AgentManifest
        {
            Name = manifestName,
            DisplayName = displayName,
            Description = string.IsNullOrWhiteSpace(entity.Description) ? $"智能体 {displayName}。" : entity.Description,
            Kind = kind,
            Avatar = entity.Avatar,
            Enabled = entity.IsEnabled,
            SortOrder = entity.SortOrder,
            AllowWorkflowMemory = entity.AllowWorkflowMemory,
            BoundPlugins = [.. entity.BoundPlugins],
            BoundMcp = [.. entity.BoundMcp],
        };

        fileService.Save(manifest, entity.Instructions);
        entity.Id = manifest.Name;
    }

    private string GetManifestKind(string name) =>
        fileService.GetByName(name)?.Manifest.Kind ?? AgentManifestKinds.Agent;

    private static bool IsSelectable(string name) => !HiddenSystemAgentNames.Contains(name);

    private string CreateAgentName(string displayName)
    {
        var candidate = string.IsNullOrWhiteSpace(displayName) ? string.Empty : displayName.Trim();
        if (IsValidManifestName(candidate) && fileService.GetByName(candidate) is null)
        {
            return candidate;
        }

        string generated;
        do
        {
            generated = $"agent-{Guid.NewGuid():N}"[..14];
        }
        while (fileService.GetByName(generated) is not null);

        return generated;
    }

    private static bool LooksLikeGeneratedId(string value) =>
        Guid.TryParseExact(value, "N", out _);

    private static bool IsValidManifestName(string value)
    {
        if (value.Length is 0 or > 64)
        {
            return false;
        }

        return value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    }

    private static AgentEntity ToEntity(AgentFileRecord record)
    {
        var manifest = record.Manifest;
        return new AgentEntity
        {
            Id = manifest.Name,
            Name = manifest.DisplayName,
            Instructions = record.Prompt,
            Description = manifest.Description,
            Avatar = manifest.Avatar,
            IsEnabled = manifest.Enabled,
            SortOrder = manifest.SortOrder,
            BoundPlugins = [.. manifest.BoundPlugins],
            BoundMcp = [.. manifest.BoundMcp],
            AllowWorkflowMemory = manifest.AllowWorkflowMemory,
        };
    }
}
