---
name: skill-writer
description: Skill creation template and guidelines for writing properly formatted skills
license: MIT
user-invocable: true
---
# Skill Writer - 技能编写指南
## Overview
本技能提供**标准技能文件格式模板**和**编写规范**，帮助开发者正确创建新的技能文件，避免格式错误导致系统无法加载。
**核心用途**：
- 提供标准 YAML Front Matter 格式
- 提供技能文件结构模板
- 提供常见格式错误检查清单
- 提供技能最佳实践指南
---
## Routing Table
| 关键词 | 描述 |
|--------|------|
| `创建技能`, `新建技能`, `写技能` | 创建新技能文件 |
| `技能模板`, `skill template` | 获取标准技能模板 |
| `技能格式`, `skill format` | 检查技能格式是否正确 |
| `YAML front matter` | 获取 YAML 头部格式规范 |
| `技能目录结构` | 获取技能文件夹结构要求 |
| `格式错误`, `加载失败` | 诊断技能格式问题 |
---
## Scope
本技能涵盖以下功能：

1. **标准模板提供** - 完整的 SKILL.md 文件模板
2. **YAML 格式规范** - 正确的 YAML Front Matter 写法
3. **目录结构规范** - 技能文件夹的标准结构
4. **格式检查清单** - 验证技能格式是否正确
5. **常见错误示例** - 展示常见格式错误及修正方法
6. **最佳实践指南** - 技能编写的最佳实践
7. **示例参考** - 提供多个技能的参考示例
8. **自动修复建议** - 针对格式错误提供修复方案
---
## Out of Scope
以下功能**不在**本技能范围内：

1. ? 技能业务逻辑设计（应使用对应领域技能）
2. ? 技能资源文件内容编写
3. ? 技能脚本代码编写
4. ? 技能测试和调试
5. ? 技能发布和部署流程
6. ? 技能性能优化
7. ? 技能版本管理策略
---
## Standard Template (标准模板)
### 完整 SKILL.md 文件模板
```markdown
---
name: skill-name
description: Brief description of what this skill does
license: MIT
user-invocable: true
---
# Skill Title
## Overview
Brief overview of the skill's purpose and capabilities.
## Routing Table
| Keyword | Description |
|---------|-------------|
| `keyword1` | What it triggers |
| `keyword2` | What it triggers |
## Scope
List of capabilities covered by this skill:
1. Capability 1
2. Capability 2
3. Capability 3
## Out of Scope
List of capabilities NOT covered:
1. ? Not covered 1
2. ? Not covered 2
## Usage Examples
### Example 1: Basic Usage
Description of the example.
### Example 2: Advanced Usage
Description of the advanced example.
## Resources
- `resource-name` - Description of the resource
## Scripts
- `script-name.ps1` - Description of the script
```
---
## YAML Front Matter 规范
### 必需字段
```yaml
---
name: skill-name              # 必需：技能名称（英文，小写，连字符分隔）
description: Description      # 必需：简短描述（英文）
license: MIT                  # 必需：许可证类型
user-invocable: true          # 必需：是否允许用户调用（true/false）
---
```
### 字段要求
| 字段 | 类型 | 要求 | 示例 |
|------|------|------|------|
| `name` | string | 必需，英文小写，连字符分隔 | `skill-writer`, `dotnet-api` |
| `description` | string | 必需，英文简短描述 | `Skill creation template` |
| `license` | string | 必需，通常 MIT | `MIT`, `Apache-2.0` |
| `user-invocable` | boolean | 必需，true 或 false（小写） | `true`, `false` |
### 常见错误
| 错误写法 | 正确写法 | 说明 |
|----------|----------|------|
| `name: Skill-Name` | `name: skill-name` | 必须小写 |
| `user-invocable: True` | `user-invocable: true` | 布尔值必须小写 |
| 没有 `---` 分隔符 | 必须有 `---` 包裹 | YAML 必须用三横线包裹 |
| YAML 不在文件第一行 | YAML 必须在开头 | 前面不能有空行或内容 |
---
## Directory Structure (目录结构)
### 标准结构
```
{工作目录}\.cortana\skills\skill-name\
├── SKILL.md              # 必需：技能定义文件
├── resources/            # 可选：资源文件夹
│   ├── references.md
│   └── assets/
└── scripts/              # 可选：脚本文件夹
    └── script.ps1
```
### 要求
1. **SKILL.md** - 必须存在，文件名必须大写
2. **resources/** - 可选，存放参考文档、资产等
3. **scripts/** - 可选，存放 PowerShell 脚本
4. **命名规范** - 文件夹和文件使用小写，连字符分隔
---
## Checklist (格式检查清单)
创建技能后，请检查以下项目：

- [ ] YAML Front Matter 在文件第一行
- [ ] YAML 使用 `---` 正确包裹（开头和结尾）
- [ ] `name` 字段为英文小写，使用连字符
- [ ] `description` 字段为英文描述
- [ ] `license` 字段存在
- [ ] `user-invocable` 为 `true` 或 `false`（小写）
- [ ] 文件名为 `SKILL.md`（大写）
- [ ] 目录名为技能名称（小写，连字符）
- [ ] Markdown 语法正确（标题、列表、表格）
- [ ] 没有使用不支持的 Markdown 扩展
---
## Common Mistakes (常见错误)
### 错误 1: YAML 格式错误
```yaml
# ? 错误：没有分隔符
name: my-skill
description: My skill
# ? 正确
---
name: my-skill
description: My skill
license: MIT
user-invocable: true
---
```
### 错误 2: 布尔值大写
```yaml
# ? 错误
user-invocable: True
# ? 正确
user-invocable: true
```
### 错误 3: 名称大小写错误
```yaml
# ? 错误
name: My-Skill
# ? 正确
name: my-skill
```
### 错误 4: YAML 不在开头
```markdown
# ? 错误：前面有内容
# 一些注释
---
name: my-skill
---
# ? 正确：YAML 在第一行
---
name: my-skill
---
```
---
## Usage Examples
### 示例 1: 创建新技能
**用户**: "帮我创建一个新的技能，用于处理 WordPress 插件开发"

**步骤**:

1. 创建目录：`{工作目录}\.cortana\skills\wp-plugin-dev`
2. 复制标准模板到 `SKILL.md`
3. 修改 `name` 为 `wp-plugin-dev`
4. 修改 `description` 为 `WordPress plugin development skill`
5. 填写业务内容（Overview, Routing Table, Scope 等）
### 示例 2: 检查技能格式
**用户**: "检查一下我的技能格式对不对"

**步骤**:

1. 读取 SKILL.md 文件
2. 检查 YAML Front Matter 是否存在且格式正确
3. 检查必需字段是否完整
4. 检查布尔值是否小写
5. 检查文件名和目录名是否符合规范
6. 输出检查结果和修复建议
### 示例 3: 修复格式错误
**用户**: "我的技能加载失败了，帮我修复"

**步骤**:

1. 读取当前 SKILL.md 内容
2. 识别格式错误（使用 Checklist）
3. 提供修复方案
4. 执行修复（需用户确认）
5. 验证修复结果
---
## Resources
- `template` - 标准技能模板（本文件即模板）
- `checklist` - 格式检查清单
- `examples` - 常见技能示例参考
## Scripts
- `validate-skill.ps1` - 技能格式验证脚本
- `create-skeleton.ps1` - 创建技能骨架脚本
---
## Best Practices (最佳实践)
1. **先复制模板** - 从现有技能（如 `dotnet-ui`）复制结构
2. **只改内容** - 保持 YAML 格式不变，只修改业务内容
3. **小写命名** - 所有名称使用小写和连字符
4. **英文描述** - YAML 中的描述使用英文
5. **逐步验证** - 每修改一步都检查格式
6. **参考现有技能** - 参考 `dotnet-*` 系列技能的格式
7. **备份原文件** - 修改前先备份原始内容
---
*Version: 1.0*
*Last Updated: 2026-04-10*
