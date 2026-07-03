---
name: skill-plugin-installation
description: '安装 Cortana 技能或插件。处理 zip 安装、目录选择、解压和结构校验。'
user-invocable: true
---

# Skill Plugin Installation

用于指导 AI 安装 Cortana 技能和插件。

## 目标

- 安装 skill 或 plugin
- 支持单个 zip 或目录批量安装
- 让脚本执行安装、解压和校验
- AI 直接组装最终安装目录传给脚本
- AI 根据脚本结果判断是否成功和失败原因

## 目录规则

- 用户数据目录中的插件：`{用户数据目录}/plugins/`
- 用户数据目录中的技能：`{用户数据目录}/skills/`
- 工作目录中的插件：`{工作目录}/.cortana/plugins/`
- 工作目录中的技能：`{工作目录}/.cortana/skills/`
- AI 负责按安装目标选择并组装最终目录

## 输入规则

- 用户必须提供 zip 文件路径或目录路径
- 如果提供目录，只处理该目录下的 `.zip`
- 一个技能或插件安装后必须对应一个文件夹
- AI 需要先确认安装到用户数据目录还是工作目录
- 复杂安装过程不要在技能文件里展开，交给脚本处理

## 执行流程

1. 确认安装对象是 skill 还是 plugin
2. 获取 zip 文件路径或目录路径
3. 提醒用户选择安装范围
4. 调用安装脚本
5. 等待用户反馈脚本执行结果
6. 根据结果判断安装成功或失败原因

## 校验规则

### 技能

- 根目录必须直接存在 `skill.md`
- `skill.md` 必须是有效的技能描述文件
- 缺少或格式明显不对时，安装失败

### 插件

- 根目录必须直接存在 `plugin.json`
- `plugin.json` 必须位于插件根目录
- 不允许再多嵌套一层插件目录
- 正确：`plugins/add-services/plugin.json`
- 错误：`plugins/add-services/add-services/plugin.json`

## 脚本

```powershell
.\skills\skill-plugin-installation\scripts\install-package.ps1
```

- 脚本负责解压、覆盖、结构校验和结果输出
- AI 不需要在技能文件里展开脚本内部实现
- 如果脚本返回失败，AI 根据失败信息继续分析

## 示例

```powershell
.\skills\skill-plugin-installation\scripts\install-package.ps1 -PackageType skill -SourcePath C:\Packages\my-skill.zip -InstallDirectory C:\Users\me\AppData\Roaming\Netor\skills
.\skills\skill-plugin-installation\scripts\install-package.ps1 -PackageType plugin -SourcePath C:\Packages\my-plugin.zip -InstallDirectory D:\Repo\.cortana\plugins
.\skills\skill-plugin-installation\scripts\install-package.ps1 -PackageType plugin -SourcePath C:\Packages -InstallDirectory C:\Users\me\AppData\Roaming\Netor\plugins
```

## 约束

- 安装前提醒用户选择目录
- 安装和校验主要由脚本完成
- AI 依据用户反馈的脚本结果判断原因
- 批量安装时，单个失败不影响其他包继续处理
