---
name: skill-plugin-installation
description: '安装或更新 Cortana 技能/插件。处理 zip 解压、结构校验、插件热更新（卸载→替换→重载）。'
user-invocable: true
---

# Skill Plugin Installation

安装或更新 Cortana 技能和插件。

## 安装目录

| 类型 | 用户数据目录 | 工作目录 |
|------|-------------|---------|
| 插件 | `{用户数据目录}/plugins/` | `{工作目录}/.cortana/plugins/` |
| 技能 | `{用户数据目录}/skills/` | `{工作目录}/.cortana/skills/` |

安装前确认用户选择哪个目录。

## 输入要求

- 用户提供 `.zip` 文件路径或包含 `.zip` 的目录路径
- 提供目录时，处理其下所有 `.zip`

## 流程

### 新安装（目标目录不存在）

1. 确认类型：skill 或 plugin
2. 获取 zip 路径
3. 确认安装目录
4. 运行安装脚本
5. **仅 plugin**：调用 `sys_reload_plugin` 加载新插件

### 更新安装（目标目录已存在）

1. 确认类型：skill 或 plugin
2. 获取 zip 路径
3. 确认安装目录
4. **仅 plugin**：调用 `sys_unload_plugin` 卸载目标插件，释放文件占用
5. 运行安装脚本（加 `-Force` 覆盖）
6. **仅 plugin**：调用 `sys_reload_plugin` 重新加载插件

### 判断是否已存在

- 调用 `sys_list_loaded_plugins` 查看已加载插件列表
- 或检查目标安装目录下是否已有同名文件夹

## 插件管理工具

| 工具 | 用途 |
|------|------|
| `sys_list_loaded_plugins` | 列出已加载的插件和目录名 |
| `sys_unload_plugin(dirName)` | 卸载插件，释放文件占用 |
| `sys_reload_plugin(dirName)` | 重新加载插件 |

**关键**：plugin 类型更新时必须先 unload 再替换文件，否则进程占用导致替换失败。skill 类型无需 unload（纯文件，无进程占用）。

## 安装脚本

```powershell
.\skills\skill-plugin-installation\scripts\install-package.ps1 -PackageType <skill|plugin> -SourcePath <path> -InstallDirectory <path> [-Force]
```

- `-Force`：覆盖已存在的目标目录
- 脚本负责解压、结构校验、结果输出
- 根据脚本返回的 `Success` 和 `Message` 判断结果

## 资源

- resources/install-flow.md

### 示例

```powershell
# 新装技能
.\skills\skill-plugin-installation\scripts\install-package.ps1 -PackageType skill -SourcePath C:\pkg\my-skill.zip -InstallDirectory C:\Users\me\AppData\Roaming\Netor\skills

# 新装插件
.\skills\skill-plugin-installation\scripts\install-package.ps1 -PackageType plugin -SourcePath C:\pkg\my-plugin.zip -InstallDirectory C:\Users\me\AppData\Roaming\Netor\plugins

# 覆盖更新插件
.\skills\skill-plugin-installation\scripts\install-package.ps1 -PackageType plugin -SourcePath C:\pkg\my-plugin.zip -InstallDirectory C:\Users\me\AppData\Roaming\Netor\plugins -Force

# 批量安装（目录下所有 zip）
.\skills\skill-plugin-installation\scripts\install-package.ps1 -PackageType plugin -SourcePath C:\pkg\ -InstallDirectory C:\Users\me\AppData\Roaming\Netor\plugins
```

## 校验规则

### 技能
- 根目录必须有 `SKILL.md` 或 `skill.md`
- 技能描述文件需含 Markdown 标题；有 YAML 头时需含 `name` 和 `description`

### 插件
- 根目录必须有 `plugin.json`（有效 JSON）
- 不允许嵌套：`plugins/foo/plugin.json` ✓ · `plugins/foo/foo/plugin.json` ✗

## 约束

- 批量安装时单个失败不阻断其余处理
- 安装和校验由脚本完成，AI 只组装参数并根据结果反馈
