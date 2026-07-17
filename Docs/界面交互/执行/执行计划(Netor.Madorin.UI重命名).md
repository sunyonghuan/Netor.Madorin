# Netor.Madorin.AvaloniaUI 重命名为 Netor.Cortana.UI : 100%

## Step 1 梳理修改点 : 100%
- [√] 扫描项目文件夹、项目文件、解决方案引用。
- [√] 扫描发布脚本与命令入口。
- [√] 扫描源码命名空间、XAML x:Class、资源名引用。
- [√] 扫描文档中的当前主线说明和发布流程。

## Step 2 重命名项目与代码命名空间 : 100%
- [√] 将 `Src/Netor.Cortana.AvaloniaUI` 重命名为 `Src/Netor.Cortana.UI`。
- [√] 将 `Netor.Madorin.AvaloniaUI.csproj` 重命名为 `Netor.Cortana.UI.csproj`。
- [√] 将源码命名空间、using、XAML `x:Class`、`clr-namespace` 从 `Netor.Madorin.AvaloniaUI` 更新为 `Netor.Cortana.UI`。
- [√] 检查嵌入资源名与项目 RootNamespace/AssemblyName。

## Step 3 更新构建与发布入口 : 100%
- [√] 更新 `Netor.Cortana.slnx` 中的项目路径。
- [√] 更新 `Build/madorin.publish.ps1` 指向新项目路径。
- [√] 将 `avaloniaui.publish/package` 脚本重命名为 `ui.publish/package`。
- [√] 更新 cmd 入口中的 ps1 文件名。
- [√] 保留发布输出目录和包名策略的兼容性判断。

## Step 4 更新文档 : 100%
- [√] 更新当前主线文档、发布流程文档文件名与内容。
- [√] 更新 README 与规划文档中的项目路径。
- [√] 更新历史发布说明中的当前路径引用。
- [√] 避免修改生成缓存、数据库、备份、bin/obj/.vs。

## Step 5 验证与收尾 : 100%
- [√] 扫描确认非生成文件不再残留旧项目名引用。
- [√] 执行 `dotnet build Netor.Cortana.slnx` 验证。
- [√] 修复验证发现的问题。
- [√] 记录实际修改文件清单。

## 实际修改清单
- `Netor.Cortana.slnx`：主 UI 项目路径改为 `Src/Netor.Cortana.UI/Netor.Cortana.UI.csproj`。
- `Src/Netor.Cortana.UI/`：承接原 `Src/Netor.Cortana.AvaloniaUI/` 的源码、资源、配置和清单文件。
- `Src/Netor.Cortana.UI/Netor.Cortana.UI.csproj`：项目文件重命名，保留 `AssemblyName`/`AssemblyTitle` 为 `Madorin`，恢复 `ApplicationIcon`。
- `Src/Netor.Cortana.UI/app.manifest`：`assemblyIdentity` 更新为 `Madorin.UI`。
- `Build/ui.publish.ps1`、`Build/ui.publish.cmd`：替代原 `avaloniaui.publish.*`。
- `Build/ui.package.ps1`、`Build/ui.package.cmd`：替代原 `avaloniaui.package.*`。
- `Build/madorin.publish.ps1`：主项目路径更新为 `Netor.Cortana.UI`。
- `README.md`、`Docs/系统流程与规划/UI-编译打包发布流程.md`、历史文档和 release notes：同步项目名、路径和脚本名。
- UI 源码与 XAML：`namespace`、`using`、`x:Class`、`clr-namespace` 更新为 `Netor.Cortana.UI`。
- 嵌入资源读取：`Netor.Cortana.UI.appsettings.json`。

## 验证结果
- `dotnet build Netor.Cortana.slnx` 已通过。
- 可维护文件扫描未发现 `Netor.Madorin.AvaloniaUI`、旧项目路径、旧项目文件名、旧 `avaloniaui.publish/package` 脚本名残留。
