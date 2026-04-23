# Git 提交记录 - 国际化 (i18n) 更新

## 提交信息

**提交日期**: 2025-01-XX  
**提交类型**: Feature / Refactoring  
**影响范围**: AI 工具描述国际化  

---

## 修改概述

本次提交将项目中所有 AI 工具的描述文本从中文翻译为英文，以实现国际化 (i18n) 支持。修改涉及 4 个文件，共计约 50+ 处工具描述文本的翻译。

---

## 修改文件清单

### 1. `Src/Netor.Cortana.AI/Providers/FileMemoryProvider.cs`

**修改内容**:
- 系统指令前缀和后缀的翻译
- 记忆管理工具描述翻译：
  - `sys_read_memory`: 读取记忆规则 → Read memory rules
  - `sys_write_memory`: 写入记忆规则 → Write memory rules
  - `sys_edit_memory`: 编辑记忆行 → Edit memory line
  - `sys_delete_memory`: 删除记忆行 → Delete memory line

**影响功能**: AI 记忆管理工具的英文描述

---

### 2. `Src/Netor.Cortana.AvaloniaUI/Providers/AiConfigToolProvider.cs`

**修改内容**:
- 注释翻译：查询 → Query, 切换默认 → Set Default, 新增 → Add New, 智能体提示词 → Agent Instructions
- AI 配置管理工具描述翻译：
  - `sys_list_providers`: 列出 AI 服务厂商 → List AI providers
  - `sys_list_agents`: 列出智能体 → List agents
  - `sys_list_models`: 列出模型 → List models
  - `sys_set_default_provider/agent/model`: 设置默认项 → Set default
  - `sys_add_provider/agent/model`: 新增配置 → Add new
  - `sys_get_agent_instructions`: 获取提示词 → Get instructions
  - `sys_update_agent_instructions`: 更新提示词 → Update instructions

**影响功能**: AI 配置管理界面的英文描述

---

### 3. `Src/Netor.Cortana.AvaloniaUI/Providers/PluginManagementProvider.cs`

**修改内容**:
- 使用指令 (`using`) 排序优化
- 插件管理工具描述翻译：
  - `sys_list_loaded_plugins`: 列出已加载插件 → List loaded plugins
  - `sys_unload_plugin`: 卸载插件 → Unload plugin
  - `sys_reload_plugin`: 重载插件 → Reload plugin
- 插件更新流程说明翻译

**影响功能**: 插件管理功能的英文描述

---

### 4. `Src/Netor.Cortana.AvaloniaUI/Providers/WindowToolProvider.cs`

**修改内容**:
- 窗口管理工具描述翻译：
  - `sys_show_main_window`: 显示主窗口 → Show main window
  - `sys_hide_main_window`: 隐藏主窗口 → Hide main window
  - `sys_show_settings_window`: 打开设置窗口 → Open settings window
  - `sys_show_float_window`: 显示浮动球 → Show floating ball
  - `sys_move_float_window`: 移动浮动球 → Move floating ball
  - `sys_get_main_window_status`: 获取主窗口状态 → Get main window status
  - `sys_get_settings_window_status`: 获取设置窗口状态 → Get settings window status
- 路径查询工具描述翻译：
  - `sys_get_workspace_directory`: 获取工作目录 → Get workspace directory
  - `sys_get_user_data_directory`: 获取用户数据目录 → Get user data directory
  - `sys_get_workspace_skills_directory`: 获取工作区技能目录 → Get workspace skills directory
  - `sys_get_workspace_plugins_directory`: 获取工作区插件目录 → Get workspace plugins directory
  - `sys_get_user_skills_directory`: 获取全局技能目录 → Get global skills directory
  - `sys_get_user_plugins_directory`: 获取全局插件目录 → Get global plugins directory
- 工作目录管理工具描述翻译：
  - `sys_change_workspace_directory`: 修改工作目录 → Change workspace directory
- 会话管理工具描述翻译：
  - `sys_new_session`: 创建新会话 → Create new session
- 注释分类翻译：路径查询 → Path Queries, 工作目录管理 → Working Directory Management, 会话管理 → Session Management

**影响功能**: 窗口管理和系统路径查询的英文描述

---

## 翻译原则

1. **准确性**: 确保技术术语准确翻译，如 "Provider" 译为 "Provider"，"Agent" 译为 "Agent"
2. **一致性**: 保持相同概念在不同文件中翻译一致
3. **简洁性**: 工具描述保持简洁明了，便于 AI 理解
4. **完整性**: 保留原有参数说明和使用示例

---

## 测试建议

- [ ] 验证所有工具在 AI 对话中能正常显示英文描述
- [ ] 测试工具调用功能是否正常
- [ ] 检查 UI 界面显示是否正确
- [ ] 确认记忆文件读写功能正常

---

## 后续工作

- [ ] 考虑添加多语言支持架构（如资源文件 .resx）
- [ ] 补充其他未翻译的中文文本
- [ ] 编写国际化开发规范文档

---

## Git 命令

```bash
# 查看修改
git diff

# 添加文件
git add Src/Netor.Cortana.AI/Providers/FileMemoryProvider.cs
git add Src/Netor.Cortana.AvaloniaUI/Providers/AiConfigToolProvider.cs
git add Src/Netor.Cortana.AvaloniaUI/Providers/PluginManagementProvider.cs
git add Src/Netor.Cortana.AvaloniaUI/Providers/WindowToolProvider.cs

# 提交
git commit -m "feat(i18n): translate AI tool descriptions to English

- Translate all AI tool descriptions from Chinese to English
- Update FileMemoryProvider memory management tools
- Update AiConfigToolProvider configuration tools  
- Update PluginManagementProvider plugin tools
- Update WindowToolProvider window and path tools
- Optimize using statements order in PluginManagementProvider

Breaks: None
Docs: Updated tool descriptions for internationalization"

# 推送
git push origin master
```

---

**提交人**: AI Assistant  
**审核人**: TBD
