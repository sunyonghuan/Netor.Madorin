using Cortana.Plugins.Memory.Models;

namespace Cortana.Plugins.Memory.Services;

/// <summary>
/// 提供记忆系统配置读取和类型转换能力。
/// </summary>
public interface IMemorySettingsService
{
    /// <summary>
    /// 获取完整记忆系统配置。
    /// </summary>
    MemoryOptions GetOptions(string? agentId = null, string? workspaceId = null);

    /// <summary>
    /// 获取记忆衰减配置。
    /// </summary>
    MemoryDecayOptions GetDecayOptions(string? agentId = null, string? workspaceId = null);

    /// <summary>
    /// 获取记忆保留配置。
    /// </summary>
    MemoryRetentionOptions GetRetentionOptions(string? agentId = null, string? workspaceId = null);

    /// <summary>
    /// 获取记忆召回配置。
    /// </summary>
    MemoryRecallOptions GetRecallOptions(string? agentId = null, string? workspaceId = null);

    /// <summary>
    /// 获取主动记忆供应配置。
    /// </summary>
    MemorySupplyOptions GetSupplyOptions(string? agentId = null, string? workspaceId = null);

    /// <summary>
    /// 获取抽象记忆生成配置。
    /// </summary>
    MemoryAbstractionOptions GetAbstractionOptions(string? agentId = null, string? workspaceId = null);

    /// <summary>
    /// 获取记忆治理审计配置。
    /// </summary>
    MemoryGovernanceOptions GetGovernanceOptions(string? agentId = null, string? workspaceId = null);

    /// <summary>
    /// 按布尔值读取配置。
    /// </summary>
    bool GetBoolean(string settingKey, bool defaultValue, string? agentId = null, string? workspaceId = null);

    /// <summary>
    /// 按整数读取配置。
    /// </summary>
    int GetInt32(string settingKey, int defaultValue, string? agentId = null, string? workspaceId = null);

    /// <summary>
    /// 按浮点数读取配置。
    /// </summary>
    double GetDouble(string settingKey, double defaultValue, string? agentId = null, string? workspaceId = null);

    /// <summary>
    /// 按字符串读取配置。
    /// </summary>
    string GetString(string settingKey, string defaultValue, string? agentId = null, string? workspaceId = null);
}
