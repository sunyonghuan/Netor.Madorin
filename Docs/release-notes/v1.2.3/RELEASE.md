# Netor.Madorin v1.2.3 发布说明

**发布日期：** 2026 年 4 月 21 日
**运行时：** .NET 10 | Avalonia 12 | Native AOT
**平台：** Windows 10/11 x64

---

## 🎯 版本亮点

本版本聚焦于 PowerShell 工具链的可发布性和稳定性，默认改为后台执行以消除窗口闪烁，并补齐超时与会话清理约束，降低 AI 调用脚本时留下僵尸进程的风险。

---

## ✨ 改进

### PowerShell 默认后台执行

- AI 通过 `sys_execute_powershell` 执行脚本时，默认使用后台模式，不再弹出 PowerShell 窗口
- 本地持久会话 `sys_start_local_session` 默认后台启动，减少打扰用户桌面
- 远程 SSH 会话按认证方式自动选择执行模式：密钥认证默认后台，密码认证保留前台以支持人工输入

### 超时与会话清理增强

- 快速执行入口即使传入 `timeout=0`，也会自动回退到 60 秒保护超时
- 后台执行场景统一启用取消定时器，超时后更积极终止进程，减少进程残留
- 会话空闲清理从 5 分钟缩短到 3 分钟，异常退出会话会更快被回收
- 关闭会话时先尝试优雅退出，超时后再强制结束进程树

### AI 工具说明统一改为英文

- PowerShell Provider 中所有面向 AI 的 Instructions 和 tool descriptions 已统一改成英文
- 保留用户可见的中文结果提示，避免影响现有桌面端交互习惯

---

## 📋 变更文件清单

- `Src/Netor.Madorin.Plugin/BuiltIn/PowerShell/PowerShellExecutor.cs` — 默认后台执行、保护超时兜底
- `Src/Netor.Madorin.Plugin/BuiltIn/PowerShell/ExecutionSession.cs` — 会话后台模式、优雅退出与强制清理
- `Src/Netor.Madorin.Plugin/BuiltIn/PowerShell/SessionRegistry.cs` — 后台参数透传、空闲会话清理增强
- `Src/Netor.Madorin.Plugin/BuiltIn/PowerShell/PowerShellProvider.cs` — AI 工具说明英文统一、后台参数暴露
- `Src/Netor.Madorin.UI/Netor.Madorin.UI.csproj` — 版本号更新到 1.2.3

---

## ⬆️ 升级说明

直接替换文件即可，无需数据库迁移。PowerShell 工具调用方如需前台可见窗口，需要显式传入 `background=false`。
