namespace Netor.Cortana.Platform.Admin.Models.Settings;

using System.ComponentModel.DataAnnotations;
using Netor.Database.Abstractions;
using Netor.Database.Abstractions.Enums;

public sealed record SettingGroupSummary(
    string Name,
    int Count,
    int BooleanCount,
    int ProtectedCount);

public sealed record SettingListItem(
    string Id,
    string Key,
    string Name,
    string Display,
    string Value,
    NetorDataType Type,
    bool IsProtection,
    string Group);

public sealed record SettingTableItem(
    string Id,
    string Key,
    string Name,
    string Display,
    string Value,
    string MaskedValue,
    NetorDataType Type,
    string TypeName,
    bool IsProtection,
    string ProtectionName,
    string Group);

public sealed class SettingsIndexViewModel
{
    public IReadOnlyList<SettingGroupSummary> Groups { get; init; } = [];

    public IReadOnlyList<SettingListItem> Items { get; init; } = [];

    public string? Keyword { get; init; }

    public string? Group { get; init; }

    public int TotalCount { get; init; }

    public int BooleanCount { get; init; }

    public int ProtectedCount { get; init; }

    public int EditableCount { get; init; }
}

public sealed class SettingsEditViewModel
{
    public string? Id { get; set; }

    [Required(ErrorMessage = "请输入设置键")]
    [StringLength(64, ErrorMessage = "设置键不能超过 64 个字符")]
    [Display(Name = "设置键")]
    public string Key { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入设置名称")]
    [StringLength(32, ErrorMessage = "设置名称不能超过 32 个字符")]
    [Display(Name = "设置名称")]
    public string Name { get; set; } = string.Empty;

    [StringLength(128, ErrorMessage = "说明不能超过 128 个字符")]
    [Display(Name = "设置说明")]
    public string? Display { get; set; }

    [StringLength(4000, ErrorMessage = "设置值不能超过 4000 个字符")]
    [Display(Name = "设置值")]
    public string Value { get; set; } = string.Empty;

    [Display(Name = "值类型")]
    public NetorDataType Type { get; set; }

    [StringLength(32, ErrorMessage = "分组不能超过 32 个字符")]
    [Display(Name = "设置分组")]
    public string? Group { get; set; }

    [Display(Name = "保护设置")]
    public bool IsProtection { get; set; }

    public bool IsCreate { get; set; }
}

public sealed class SettingsBatchUpdateViewModel
{
    public string? Keyword { get; set; }

    public string? Group { get; set; }

    public List<SettingValueUpdateInput> Items { get; set; } = [];
}

public sealed class SettingValueUpdateInput
{
    public string Id { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

public sealed class SettingsDetailViewModel
{
    public string Id { get; init; } = string.Empty;

    public string Key { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Display { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;

    public NetorDataType Type { get; init; }

    public string TypeName { get; init; } = string.Empty;

    public string Group { get; init; } = string.Empty;

    public bool IsProtection { get; init; }
}
