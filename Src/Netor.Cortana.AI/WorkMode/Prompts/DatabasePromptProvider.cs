using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.WorkMode.Prompts;

/// <summary>
/// 从数据库 SystemSettings 表加载提示词。
/// Key 格式：prompt.work_mode.&lt;name&gt;（如 "prompt.work_mode.general_manager"）。
/// </summary>
public sealed class DatabasePromptProvider : IPromptProvider
{
    private readonly SystemSettingsService _settingsService;

    public DatabasePromptProvider(SystemSettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    public Task<string?> GetPromptAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult<string?>(null);

        var key = $"prompt.work_mode.{name}";
        var value = _settingsService.GetValue(key);
        return Task.FromResult(value);
    }
}
