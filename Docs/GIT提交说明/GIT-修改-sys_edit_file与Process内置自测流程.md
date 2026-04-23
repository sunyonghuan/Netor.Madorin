# Git 提交记录 - sys_edit_file 与 Process 内置自测流程

## 提交信息

**提交日期**: 2026-04-23  
**提交类型**: Feature / Refactoring / Docs  
**影响范围**: 文件工具安全编辑能力、Process 插件测试规范与脚手架模板

---

## 修改概述

本次提交完成两组相关收口：

1. 为内置文件工具补齐安全的“读取指定行 + 按行编辑”工作流，新增 `sys_edit_file`，同时增强 `sys_read_file` 返回行号、范围、编码、换行和内容哈希。
2. 将 C# Process 插件的本地测试规范统一为“插件项目内置自测入口”，要求默认在项目自身通过 `Program.cs` + `SelfTest.cs` 执行 `dotnet run -- --self-test`，不再默认创建外部脚本或额外测试工程。

---

## AOT 风险评估

### 1. 文件工具增强

- 本次文件工具改动未引入反射扫描、动态代码生成或运行时代码编译。
- 新增的文本处理逻辑仅使用 `System.IO`、`System.Text`、`System.Security.Cryptography` 等 BCL 能力。
- 结论：当前改动未引入新的 Native AOT 明显风险。

### 2. Process 插件内置自测模板

- `SelfTest.cs` 被明确限制在 `#if DEBUG` 条件编译内。
- `Program.cs` 中的 `--self-test` 分支在非 Debug 构建下直接拒绝执行。
- 结论：测试入口不会进入 Release / AOT 发布路径，不污染正式发布产物。

---

## 修改文件清单

### 1. 文件读取与按行编辑增强

- `Src/Netor.Cortana.Plugin/BuiltIn/FileBrowser/FileOperator.cs`
- `Src/Netor.Cortana.Plugin/BuiltIn/FileBrowser/FileBrowser.cs`
- `Src/Netor.Cortana.Plugin/BuiltIn/FileBrowser/FileBrowserProvider.cs`
- `Src/Netor.Cortana.Plugin/BuiltIn/FileBrowser/FileOperationProvider.cs`
- `Src/Netor.Cortana.Plugin/BuiltIn/FileBrowser/FileItemInfo.cs`

**修改内容**:

- 新增 `sys_edit_file` 对应的按行编辑能力，支持 `replace / insert / delete`。
- 统一文本文件读写逻辑，保留原文件编码、BOM 与换行风格。
- 为读取结果补充结构化元数据：总行数、范围、编码、换行、哈希、行内容。
- 为编辑操作增加 `expectedHash` 校验，避免基于旧内容误改文件。
- `sys_write_file` 在覆盖已有文件时改为尽量保留原编码与换行，而不是直接用统一 UTF-8 重写。

**影响功能**:

- AI 可以先精确读取行号，再对指定行安全编辑。
- 代码、配置文件和包含特殊字符的文本修改风险显著降低。
- 文件工具链从“整文件粗暴读写”收口为“读范围 -> 校验哈希 -> 按行编辑”。

### 2. Process 插件测试规范与脚手架收口

- `.github/skills/plugin-development/SKILL.md`
- `skills/plugin-development/subskills/process/SKILL.md`
- `skills/plugin-development/subskills/process/resources/csharp-process-plugin.md`
- `skills/plugin-development/scripts/create-process-plugin.ps1`

**修改内容**:

- 总技能说明增加“C# Process 插件优先用项目内置自测入口”的统一约束。
- Process 子技能将测试流程改为固定顺序：`dotnet build` -> `dotnet run -- --self-test` -> `publish`。
- 子技能与参考文档补充 `Program.cs` 与 `SelfTest.cs` 的最小模板。
- 脚手架现在直接生成自测分支模板和 `SelfTest.cs` 文件。
- 明确禁止 AI 在默认情况下创建外部脚本、额外控制台工程或独立测试工程。

**影响功能**:

- AI 在 Process 插件场景下的默认测试路径被钉死到插件项目自身。
- 测试入口更接近开发者真实工作流，减少无效脚本和额外工程。
- 自测结果可通过退出码表达成功失败，便于自动迭代。

### 3. 提交文档

- `Docs/GIT-修改-sys_edit_file与Process内置自测流程.md`

**修改内容**:

- 记录本次提交范围、验证结果、AOT 评估和建议提交命令。

---

## 验证记录

- [x] `Src/Netor.Cortana.Plugin/Netor.Cortana.Plugin.csproj` 执行 `dotnet build -c Debug -v m` 通过。
- [x] 新增 `sys_edit_file` / 增强 `sys_read_file` 相关源码无编译错误或新增警告。
- [x] `skills/plugin-development/scripts/create-process-plugin.ps1` 的 PowerShell 语法已校验通过。
- [x] 技能文档与参考文档错误检查通过。

### 本次未做的验证

- 未对 `create-process-plugin.ps1` 真实生成出的样板工程再次执行一次端到端 `dotnet run -- --self-test` 验证。
- 未为 `sys_edit_file` 补充自动化测试项目。

---

## 提交范围说明

本次建议只提交以下文件：

- `Src/Netor.Cortana.Plugin/BuiltIn/FileBrowser/FileOperator.cs`
- `Src/Netor.Cortana.Plugin/BuiltIn/FileBrowser/FileBrowser.cs`
- `Src/Netor.Cortana.Plugin/BuiltIn/FileBrowser/FileBrowserProvider.cs`
- `Src/Netor.Cortana.Plugin/BuiltIn/FileBrowser/FileOperationProvider.cs`
- `Src/Netor.Cortana.Plugin/BuiltIn/FileBrowser/FileItemInfo.cs`
- `.github/skills/plugin-development/SKILL.md`
- `skills/plugin-development/subskills/process/SKILL.md`
- `skills/plugin-development/subskills/process/resources/csharp-process-plugin.md`
- `skills/plugin-development/scripts/create-process-plugin.ps1`
- `Docs/GIT-修改-sys_edit_file与Process内置自测流程.md`

本次不建议提交当前工作树中的其他样例、生成物、日志或与本次任务无关的改动。

---

## 建议提交信息

```bash
feat(plugin): add safe line editing and process self-test workflow
```

建议提交描述：

```text
- add sys_edit_file and enrich sys_read_file with line/hash metadata
- preserve encoding and newline style for text file updates
- standardize C# process plugin testing on in-project self-test entry
- update process skill docs and scaffolding templates
```

---

## Git 命令

```bash
git add Src/Netor.Cortana.Plugin/BuiltIn/FileBrowser/FileOperator.cs
git add Src/Netor.Cortana.Plugin/BuiltIn/FileBrowser/FileBrowser.cs
git add Src/Netor.Cortana.Plugin/BuiltIn/FileBrowser/FileBrowserProvider.cs
git add Src/Netor.Cortana.Plugin/BuiltIn/FileBrowser/FileOperationProvider.cs
git add Src/Netor.Cortana.Plugin/BuiltIn/FileBrowser/FileItemInfo.cs
git add .github/skills/plugin-development/SKILL.md
git add skills/plugin-development/subskills/process/SKILL.md
git add skills/plugin-development/subskills/process/resources/csharp-process-plugin.md
git add skills/plugin-development/scripts/create-process-plugin.ps1
git add Docs/GIT-修改-sys_edit_file与Process内置自测流程.md

git commit -m "feat(plugin): add safe line editing and process self-test workflow"
```

---

**提交人**: GitHub Copilot  
**审核人**: TBD
