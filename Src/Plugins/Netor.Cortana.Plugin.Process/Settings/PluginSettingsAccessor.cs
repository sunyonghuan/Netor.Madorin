namespace Netor.Cortana.Plugin.Process.Settings;

/// <summary>
/// <see cref="PluginSettings"/> 的延迟访问桥接。
/// <para>
/// DI 容器在 <c>init</c> 请求到达前已构建完成，但此时 <see cref="PluginSettings"/> 还未知。
/// 本类作为 Singleton 注册到 DI，在 <c>init</c> 时被赋值，
/// 工具类通过 DI 注入 <see cref="PluginSettings"/> 时延迟解析。
/// </para>
/// <para>
/// 公开访问级别是为了让 Generator 生成的 <c>Program.g.cs</c> 可见。
/// 用户不应直接使用本类型，工具代码只需注入 <see cref="PluginSettings"/>。
/// </para>
/// </summary>
public sealed class PluginSettingsAccessor
{
    private PluginSettings? _value;

    /// <summary>获取已注入的配置。<c>init</c> 之前访问会抛异常。</summary>
    public PluginSettings Value =>
        _value ?? throw new InvalidOperationException(
            "PluginSettings 尚未初始化。工具方法只能在 init 消息到达后调用。");

    /// <summary>由 <c>ProcessPluginHost</c> 在处理 <c>init</c> 时设置。</summary>
    public void Set(PluginSettings value) => _value = value;

    /// <summary>检查是否已初始化（供日志组件判断是否可打开文件）。</summary>
    public bool IsInitialized => _value is not null;
}
