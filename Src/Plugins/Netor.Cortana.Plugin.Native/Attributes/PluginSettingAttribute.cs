namespace Netor.Cortana.Plugin.Native;

/// <summary>
/// 声明插件需要由宿主保存并在运行时注入的配置项。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class PluginSettingAttribute : Attribute
{
    /// <summary>插件内唯一配置键。</summary>
    public required string Key { get; init; }

    /// <summary>设置界面显示名称。</summary>
    public string? Label { get; init; }

    /// <summary>给用户看的配置说明。</summary>
    public string? Description { get; init; }

    /// <summary>配置项类型。</summary>
    public PluginSettingType Type { get; init; } = PluginSettingType.String;

    /// <summary>默认值。Generator 会按 <see cref="Type"/> 写成对应 JSON 类型。</summary>
    public string? DefaultValue { get; init; }

    /// <summary>是否必填。</summary>
    public bool Required { get; init; }

    /// <summary>是否敏感，敏感值不得回显或进入日志。</summary>
    public bool Sensitive { get; init; }

    /// <summary>支持的配置作用域。</summary>
    public string[] Scope { get; init; } = [];

    /// <summary>枚举选项。</summary>
    public string[] Options { get; init; } = [];

    /// <summary>数值最小值；仅在不是 NaN 时输出到 validation.min。</summary>
    public double ValidationMin { get; init; } = double.NaN;

    /// <summary>数值最大值；仅在不是 NaN 时输出到 validation.max。</summary>
    public double ValidationMax { get; init; } = double.NaN;

    /// <summary>字符串最小长度；仅在大于等于 0 时输出到 validation.minLength。</summary>
    public int ValidationMinLength { get; init; } = -1;

    /// <summary>字符串最大长度；仅在大于等于 0 时输出到 validation.maxLength。</summary>
    public int ValidationMaxLength { get; init; } = -1;

    /// <summary>字符串正则校验表达式。</summary>
    public string? ValidationPattern { get; init; }

    /// <summary>修改后是否要求下次启动才生效。</summary>
    public bool RestartRequired { get; init; }
}
