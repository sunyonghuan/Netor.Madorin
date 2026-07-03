---
name: skill-plugin-installation
description: 安装 Madorin skill 或 plugin；处理 zip/目录批量安装、解压、覆盖和结构校验。
user-invocable: true
---

# Skill Plugin Installation

## Scope
- 安装 skill
- 安装 plugin
- 支持单个 zip 或目录批量安装
- 安装逻辑交给脚本 `scripts/install-package.ps1`

## Directory Rules

### plugin
- 不询问安装位置。
- 直接调用 `sys_get_user_plugins_directory`。
- 安装到全局插件目录。

### skill
- 必须询问安装位置：
  1. 工作区技能目录：调用 `sys_get_workspace_skills_directory`
  2. 全局技能目录：调用 `sys_get_user_skills_directory`
- 不允许手动拼接安装目录。

## Input Rules
- 用户必须提供 zip 文件路径或目录路径。
- 目录输入只处理目录下的 `.zip`。
- 用户明确 package type 时直接使用。
- 用户未明确时：
  - 包内有 `SKILL.md` 或 `skill.md` → skill
  - 包内有 `plugin.json` → plugin
  - 无法判断 → 询问用户

## Flow
1. 获取 `SourcePath`。
2. 判断 `PackageType`: `skill` / `plugin`。
3. 如果 `PackageType=plugin`：
   - 调用 `sys_get_user_plugins_directory` 获取 `InstallDirectory`。
   - 调用 `sys_list_loaded_plugins` 检查目标插件是否已加载。
   - 如果已加载：先调用 `sys_unload_plugin`。
   - 调用脚本：`scripts/install-package.ps1`。
   - 安装/更新成功后调用 `sys_reload_plugin`。
   - 提醒用户：插件重载后需切换一下智能体才会生效。
4. 如果 `PackageType=skill`：
   - 询问安装到工作区还是全局。
   - 工作区 → `sys_get_workspace_skills_directory`
   - 全局 → `sys_get_user_skills_directory`
   - 调用脚本：`scripts/install-package.ps1`。
5. 根据脚本 JSON/文本结果判断成功或失败。

## Validation

### skill
- 根目录必须直接存在 `SKILL.md` 或 `skill.md`。
- 技能描述文件必须有效。

### plugin
- 根目录必须直接存在 `plugin.json`。
- 不允许多嵌套一层。
- 正确：`plugins/{plugin-id}/plugin.json`
- 错误：`plugins/{plugin-id}/{plugin-id}/plugin.json`

## Script
```powershell
scripts\install-package.ps1
```

- 脚本路径必须是相对于当前技能根目录的相对路径。
- 不要添加技能名、`skills/`、工作区或用户目录前缀。
- 脚本负责解压、覆盖、结构校验和结果输出。

## Commands

### install plugin
```powershell
& "scripts\install-package.ps1" -PackageType plugin -SourcePath "{zip-or-directory}" -InstallDirectory "{sys_get_user_plugins_directory}"
```

### install skill to workspace
```powershell
& "scripts\install-package.ps1" -PackageType skill -SourcePath "{zip-or-directory}" -InstallDirectory "{sys_get_workspace_skills_directory}"
```

### install skill to global
```powershell
& "scripts\install-package.ps1" -PackageType skill -SourcePath "{zip-or-directory}" -InstallDirectory "{sys_get_user_skills_directory}"
```

## Hard Rules
- plugin：禁止询问安装目录，始终全局安装。
- plugin 已加载：必须先 `sys_list_loaded_plugins` → `sys_unload_plugin` → 安装/更新 → `sys_reload_plugin`。
- plugin 重载后：必须提醒用户切换智能体才会生效。
- skill：必须询问工作区/全局。
- 安装目录必须来自系统工具。
- 脚本路径必须使用相对路径 `scripts/install-package.ps1`。
- 批量安装时单个失败不影响其他包。
