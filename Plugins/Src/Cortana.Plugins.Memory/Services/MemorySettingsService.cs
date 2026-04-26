using System.Globalization;
using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Storage;

namespace Cortana.Plugins.Memory.Services;

/// <summary>
/// 从记忆配置表读取配置，并转换为强类型配置对象。
/// </summary>
public sealed class MemorySettingsService(IMemoryStore store) : IMemorySettingsService
{
    public MemoryOptions GetOptions(string? agentId = null, string? workspaceId = null)
    {
        return new MemoryOptions
        {
            Decay = GetDecayOptions(agentId, workspaceId),
            Retention = GetRetentionOptions(agentId, workspaceId),
            Recall = GetRecallOptions(agentId, workspaceId),
            Supply = GetSupplyOptions(agentId, workspaceId),
            Abstraction = GetAbstractionOptions(agentId, workspaceId),
            Governance = GetGovernanceOptions(agentId, workspaceId)
        };
    }

    public MemoryDecayOptions GetDecayOptions(string? agentId = null, string? workspaceId = null)
    {
        return new MemoryDecayOptions
        {
            Enabled = GetBoolean("decay.enabled", true, agentId, workspaceId),
            DefaultRate = GetDouble("decay.defaultRate", 0.015, agentId, workspaceId),
            ScanIntervalMinutes = GetInt32("decay.scanIntervalMinutes", 60, agentId, workspaceId)
        };
    }

    public MemoryRetentionOptions GetRetentionOptions(string? agentId = null, string? workspaceId = null)
    {
        return new MemoryRetentionOptions
        {
            MinimumScore = GetDouble("retention.minimumScore", 0.2, agentId, workspaceId),
            ForgetThreshold = GetDouble("retention.forgetThreshold", 0.05, agentId, workspaceId)
        };
    }

    public MemoryRecallOptions GetRecallOptions(string? agentId = null, string? workspaceId = null)
    {
        return new MemoryRecallOptions
        {
            MaxWindowCount = GetInt32("recall.maxWindowCount", 6, agentId, workspaceId),
            MaxMemoryCount = GetInt32("recall.maxMemoryCount", 20, agentId, workspaceId),
            MinimumConfidence = GetDouble("recall.minimumConfidence", 0.35, agentId, workspaceId),
            IncludeCandidateMemories = GetBoolean("recall.includeCandidateMemories", false, agentId, workspaceId)
        };
    }

    public MemorySupplyOptions GetSupplyOptions(string? agentId = null, string? workspaceId = null)
    {
        return new MemorySupplyOptions
        {
            Enabled = GetBoolean("supply.enabled", true, agentId, workspaceId),
            MaxMemoryCount = GetInt32("supply.maxMemoryCount", 8, agentId, workspaceId)
        };
    }

    public MemoryAbstractionOptions GetAbstractionOptions(string? agentId = null, string? workspaceId = null)
    {
        return new MemoryAbstractionOptions
        {
            Enabled = GetBoolean("abstraction.enabled", true, agentId, workspaceId),
            MinimumSupportCount = GetInt32("abstraction.minimumSupportCount", 3, agentId, workspaceId),
            MinimumConfidence = GetDouble("abstraction.minimumConfidence", 0.55, agentId, workspaceId)
        };
    }

    public MemoryGovernanceOptions GetGovernanceOptions(string? agentId = null, string? workspaceId = null)
    {
        return new MemoryGovernanceOptions
        {
            AuditEnabled = GetBoolean("governance.auditEnabled", true, agentId, workspaceId)
        };
    }

    public bool GetBoolean(string settingKey, bool defaultValue, string? agentId = null, string? workspaceId = null)
    {
        var value = GetString(settingKey, string.Empty, agentId, workspaceId);
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        if (bool.TryParse(value, out var parsed)) return parsed;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return number != 0;
        return defaultValue;
    }

    public int GetInt32(string settingKey, int defaultValue, string? agentId = null, string? workspaceId = null)
    {
        var value = GetString(settingKey, string.Empty, agentId, workspaceId);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
    }

    public double GetDouble(string settingKey, double defaultValue, string? agentId = null, string? workspaceId = null)
    {
        var value = GetString(settingKey, string.Empty, agentId, workspaceId);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
    }

    public string GetString(string settingKey, string defaultValue, string? agentId = null, string? workspaceId = null)
    {
        if (string.IsNullOrWhiteSpace(settingKey)) return defaultValue;

        var settings = store.GetMemorySettings(agentId, workspaceId);
        var value = settings
            .GroupBy(static s => s.SettingKey, StringComparer.OrdinalIgnoreCase)
            .Select(static g => g.Last())
            .FirstOrDefault(s => string.Equals(s.SettingKey, settingKey, StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(value?.SettingValue) ? defaultValue : value.SettingValue;
    }
}
