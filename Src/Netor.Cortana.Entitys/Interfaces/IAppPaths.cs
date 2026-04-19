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

    /// <summary>工作区插件目录。</summary>
    string WorkspacePluginsDirectory { get; }

    /// <summary>用户数据技能目录。</summary>
    string UserSkillsDirectory { get; }

    /// <summary>用户数据插件目录。</summary>
    string UserPluginsDirectory { get; }

    /// <summary>插件目录。</summary>
    string PluginDirectory { get; }

    /// <summary>工作区资源根目录（.cortana/resources）。</summary>
    string WorkspaceResourcesDirectory { get; }

    /// <summary>聊天历史资源目录（.cortana/resources/histories）。</summary>
    string HistoryResourcesDirectory { get; }
}
