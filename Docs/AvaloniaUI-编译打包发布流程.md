# AvaloniaUI 编译打包发布流程

## 目标

- 将发布动作拆分为三个独立阶段：编译目录产物、打包压缩文件、发布 GitHub Release。
- 每个阶段都可以单独执行，避免每次编译后都必须立刻发布到 GitHub。
- 固定当前 AvaloniaUI 主线的标准操作步骤，减少临时命令和误操作。

## 适用范围

- 主项目：AvaloniaUI 桌面端。
- 输出目录：Realases/AvaloniaUI。
- 打包产物：Realases/Netor.Cortana-v版本号-win-x64.zip。
- 校验文件：Realases/Netor.Cortana-v版本号-win-x64.sha256。
- 发布说明：Docs/release-notes/v版本号/RELEASE.md。

## 阶段一：编译目录产物

用途：生成 AvaloniaUI 的可运行目录，不生成 zip，不接触 GitHub Release。

执行命令：

```powershell
powershell -ExecutionPolicy Bypass -File .\avaloniaui.publish.ps1
```

预期结果：

- 目录 Realases/AvaloniaUI 已存在并被刷新。
- 目录内应至少包含 Cortana.exe 和 Cortana.NativeHost.exe。
- 这一步只负责目录产物，不生成压缩包，不生成 sha256，不创建 GitHub Release。

## 阶段二：打包 zip 和 sha256

用途：将现有目录产物压缩为交付包，并生成校验文件。

执行命令：

```powershell
powershell -ExecutionPolicy Bypass -File .\avaloniaui.package.ps1
```

可选参数：

```powershell
powershell -ExecutionPolicy Bypass -File .\avaloniaui.package.ps1 -Version 1.1.6
```

预期结果：

- 生成 Realases/Netor.Cortana-v版本号-win-x64.zip。
- 生成 Realases/Netor.Cortana-v版本号-win-x64.sha256。
- 这一步只消费 Realases/AvaloniaUI 目录，不重新编译，不发布到 GitHub。

## 阶段三：创建 GitHub Release

用途：基于现有 zip、sha256 和 release notes 创建 GitHub Release。

先做参数检查：

```powershell
powershell -ExecutionPolicy Bypass -File .\github.release.ps1 -Tag v1.1.6-r2 -ValidateOnly
```

确认无误后正式发布：

```powershell
powershell -ExecutionPolicy Bypass -File .\github.release.ps1 -Tag v1.1.6-r2
```

预期结果：

- 使用现有 tag、zip、sha256 和 RELEASE.md 创建 Release。
- 不重新编译，不重新打包。
- 如果 release 已存在，脚本会直接拒绝复用该 tag。

## 标准顺序

标准发布顺序如下：

1. 运行 avaloniaui.publish.ps1，生成目录产物。
2. 运行 avaloniaui.package.ps1，生成 zip 和 sha256。
3. 运行 github.release.ps1 -ValidateOnly，先做检查。
4. 运行 github.release.ps1 -Tag xxx，正式创建 GitHub Release。

## 常见场景

只想重新打包，不想重新编译：

```powershell
powershell -ExecutionPolicy Bypass -File .\avaloniaui.package.ps1
```

只想检查 Release 参数，不想真正发版：

```powershell
powershell -ExecutionPolicy Bypass -File .\github.release.ps1 -Tag v1.1.6-r2 -ValidateOnly
```

只想重新发一个新 tag 的 Release，不重新编译也不重新打包：

```powershell
powershell -ExecutionPolicy Bypass -File .\github.release.ps1 -Tag v1.1.6-r3
```

## 注意事项

- 当前仓库 GitHub Release 存在 immutable release 历史限制，旧 tag 失败后不要强行复用。
- 更稳妥的做法是使用新的未占用 tag，例如 v1.1.6-r2、v1.1.6-r3。
- github.release.ps1 默认远端使用 github，而不是 origin。
- 本地数据库文件 cortana.db、cortana.db-wal、cortana.db-shm 不应提交到 Git。

## 相关脚本

- avaloniaui.publish.ps1：只生成 AvaloniaUI 目录产物。
- avaloniaui.package.ps1：只从 Realases/AvaloniaUI 生成 zip 和 sha256。
- github.release.ps1：只创建 GitHub Release。
- avaloniaui.publish.cmd、avaloniaui.package.cmd、github.release.cmd：对应的命令行入口。







