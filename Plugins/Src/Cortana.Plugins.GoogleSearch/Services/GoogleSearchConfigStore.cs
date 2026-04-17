using System.Text.Json;
using Cortana.Plugins.GoogleSearch.Models;
using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.GoogleSearch.Services;

/// <summary>
/// 负责从插件数据目录读写配置文件。
/// 职责单一：只关心配置文件的序列化和反序列化。
/// 内存缓存首次加载的配置，后续访问直接读缓存，避免重复磁盘 I/O。
/// </summary>
public sealed class GoogleSearchConfigStore
{
    private readonly string _configFilePath;
    private readonly ILogger<GoogleSearchConfigStore> _logger;

    /// <summary>
    /// 已加载的配置在内存中的缓存。
    /// 初始化后不再改变，直到调用 Save() 时刷新。
    /// </summary>
    private GoogleSearchConfig? _cachedConfig;

    public GoogleSearchConfigStore(
        string dataDirectory,
        ILogger<GoogleSearchConfigStore> logger)
    {
        _configFilePath = Path.Combine(dataDirectory, "config.json");
        _logger = logger;
    }

    /// <summary>
    /// 返回当前配置，优先使用内存缓存。
    /// 首次调用时从磁盘加载并缓存。
    /// </summary>
    public GoogleSearchConfig Load()
    {
        if (_cachedConfig != null)
        {
            return _cachedConfig;
        }

        if (!File.Exists(_configFilePath))
        {
            _logger.LogInformation("配置文件不存在，将返回空配置: {Path}", _configFilePath);
            _cachedConfig = new GoogleSearchConfig();
            return _cachedConfig;
        }

        try
        {
            var json = File.ReadAllText(_configFilePath);
            _cachedConfig = JsonSerializer.Deserialize(json, PluginJsonContext.Default.GoogleSearchConfig)
                ?? new GoogleSearchConfig();
            _logger.LogInformation("已加载配置文件: {Path}", _configFilePath);
            return _cachedConfig;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "配置文件解析失败，将返回空配置: {Path}", _configFilePath);
            _cachedConfig = new GoogleSearchConfig();
            return _cachedConfig;
        }
    }

    /// <summary>
    /// 将配置写入数据目录的 config.json 文件，并更新内存缓存。
    /// </summary>
    public void Save(GoogleSearchConfig config)
    {
        var directory = Path.GetDirectoryName(_configFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(config, PluginJsonContext.Default.GoogleSearchConfig);
        File.WriteAllText(_configFilePath, json);
        _cachedConfig = config;
        _logger.LogInformation("已保存配置文件: {Path}", _configFilePath);
    }

    /// <summary>
    /// 获取配置文件路径，供调用方在返回结果中展示。
    /// </summary>
    public string GetConfigFilePath() => _configFilePath;
}