namespace Netor.Cortana.AvaloniaUI;

/// <summary>
/// 应用程序路径的 Avalonia 实现，委托到 <see cref="App"/> 的静态属性。
/// </summary>
internal sealed class AppPaths : IAppPaths
{
    /// <inheritdoc />
    public string WorkspaceDirectory => App.WorkspaceDirectory;

    /// <inheritdoc />
    public string UserDataDirectory => App.UserDataDirectory;

    /// <inheritdoc />
    public string WorkspaceSkillsDirectory => Path.Combine(App.WorkspaceDirectory, ".cortana", "skills");

    /// <inheritdoc />
    public string WorkspacePluginsDirectory => Path.Combine(App.WorkspaceDirectory, ".cortana", "plugins");

    /// <inheritdoc />
    public string UserSkillsDirectory => Path.Combine(App.UserDataDirectory, "skills");

    /// <inheritdoc />
    public string UserPluginsDirectory => Path.Combine(App.UserDataDirectory, "plugins");

    /// <inheritdoc />
    public string PluginDirectory => WorkspacePluginsDirectory;

    /// <inheritdoc />
    public string WorkspaceResourcesDirectory => Path.Combine(App.WorkspaceDirectory, ".cortana", "resources");

    /// <inheritdoc />
    public string HistoryResourcesDirectory => Path.Combine(App.WorkspaceDirectory, ".cortana", "resources", "histories");
}
