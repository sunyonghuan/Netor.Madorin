# Netor.Cortana v1.2.7 发布说明

**发布日期：** 2026 年 4 月 24 日  
**运行时：** .NET 10 | Avalonia 12 | Native AOT  
**平台：** Windows 10/11 x64

---

## 🎯 版本亮点

本版本聚焦于文件工具链的 AI 返回结果统一化，将文件写入、批量写入等工具输出收敛为单一结构化模型，方便上层 AI 直接消费、路由和记录。

同时补齐了对应的提交说明与本地打包流程，当前仅生成本地发布产物，不执行 GitHub Release 上传。

---

## ✨ 新功能

### 文件工具统一结构化返回

- `FileOperationProvider` 中的文件工具输出统一为单一 JSON 模型，字段包含 `tool`、`success`、`path`、`targetPath`、`message`、`error`、`backupPath`、`operation`、`bytesWritten`、`startLine`、`endLine`、`changedLineCount`、`successCount`、`failCount`、`items`
- `sys_create_file`、`sys_write_file`、`sys_delete_file`、`sys_move_file`、`sys_create_directory`、`sys_delete_directory` 的输出都使用同一结构，便于 AI 统一解析
- `sys_write_large_file` 与 `sys_write_files_batch` 的返回结果也收敛为结构化模型，避免不同工具各自使用不同 schema
- 批量写入结果的每一项也复用相同的条目模型，方便在日志、审计或继续推理时直接消费

### 文件操作器返回结果结构化

- `FileOperator` 中的大文件写入和批量写入结果改为强类型 record
- 大文件写入可返回 `success`、`path`、`bytesWritten`、`error`、`backupPath`
- 批量写入可返回 `results`、`successCount`、`failCount`

---

## 🧭 AOT 风险评估

- 未引入反射型 JSON 序列化路径
- 统一返回模型由手写 JSON 组装输出，避免运行时多态序列化问题
- 当前变更保持 Native AOT 友好，不依赖额外运行时反射元数据

---

## 📋 变更文件清单

### 新增

- `Docs/release-notes/v1.2.7/RELEASE.md` — 本发布说明
- `Docs/GIT提交说明/GIT-修改-文件工具统一结构化与1.2.7发布.md` — 提交说明

### 修改

- `Src/Netor.Cortana.AvaloniaUI/Netor.Cortana.AvaloniaUI.csproj` — 版本号升级到 1.2.7
- `Src/Netor.Cortana.Plugin/BuiltIn/FileBrowser/FileOperationProvider.cs` — 文件工具统一结构化返回
- `Src/Netor.Cortana.Plugin/BuiltIn/FileBrowser/FileOperator.cs` — 大文件写入与批量写入改为结构化结果

---

## ⬆️ 升级说明

- 本次更新不涉及数据库迁移
- 文件工具输出现在全部遵循统一结构，AI 调用方可以直接按字段读取结果
- 本次仅进行本地发布，不创建 GitHub Release

---

## 📦 本地发布产物

- `Realases/Netor.Cortana-v1.2.7-win-x64.zip`
- `Realases/Netor.Cortana-v1.2.7-win-x64.sha256`