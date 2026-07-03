namespace Netor.Cortana.Entitys;

/// <summary>
/// 应用程序路径契约，替代 App.xxx 静态属性。
/// 由 UI 壳实现并注册到 DI，各业务层通过构造函数注入获取路径。
/// </summary>
public interface IAppPaths
{
    /// <summary>工作区目录（用户可配置）。</summary>
    string WorkspaceDirectory { get; }

    /// <summary>用户数据目录（exe 所在目录）。</summary>
    string UserDataDirectory { get; }

    /// <summary>工作区技能目录。</summary>
    string WorkspaceSkillsDirectory { get; }

    /// <summary>工作区插件目录。已废弃，插件统一安装到用户全局插件目录。</summary>
    [Obsolete("工作区插件目录已废弃，请使用 UserPluginsDirectory。")]
    string WorkspacePluginsDirectory { get; }

    /// <summary>用户数据技能目录。</summary>
    string UserSkillsDirectory { get; }

    /// <summary>用户数据插件目录。</summary>
    string UserPluginsDirectory { get; }

    /// <summary>用户数据智能体目录。</summary>
    string UserAgentsDirectory { get; }

    /// <summary>用户数据解决方案目录。</summary>
    string UserSolutionsDirectory { get; }

    /// <summary>插件目录。</summary>
    string PluginDirectory { get; }

    /// <summary>工作区资源根目录（.cortana/resources）。</summary>
    string WorkspaceResourcesDirectory { get; }

    /// <summary>聊天历史资源目录（.cortana/resources/histories）。</summary>
    string HistoryResourcesDirectory { get; }

    /// <summary>提示词目录（用户数据目录下的 prompts 子目录）。</summary>
    string PromptsDirectory { get; }
}
