# Madorin AI Runtime SampleHost

SampleHost 是通过 Client SDK 连接 Madorin AI Runtime 的参考宿主。当前包含阶段 2 多实例进程测试入口：单个宿主进程可启动、认证并重连两个独立 Runtime 子进程；完整业务宿主链路仍在后续阶段补齐。

`multi-instance` 入口由端到端测试驱动，两个 Runtime 分别使用独立工作区、实例 ID、IPC 前缀和握手 secret。Host 通过受控 stdin 句柄把两个 secret 交给自身和 Runtime 子进程，不会写入命令行、环境变量或标准输出。

## 当前实现状态

V1 阶段 0-7 的实现基线已完成，当前进入阶段 8 验收发布与质量门禁：

| 阶段 | 状态 | 当前进度 |
| --- | --- | ---: |
| 阶段 0：项目骨架基线 | 已完成 | 100% |
| 阶段 1：工程与协议基线 | 已完成 | 80% |
| 阶段 2：Runtime 骨架与双通道 | 执行中 | 98.6% |
| 阶段 3：Run 状态机与持久化基线 | 已完成 | 70% |
| 阶段 4：Provider 适配层 | 已完成 | 75% |
| 阶段 4A：独立配置与选择 | 已完成 | 70% |
| 阶段 4B：全局与项目记忆 | 已完成 | 75% |
| 阶段 5：工具权限与反向 RPC | 已完成 | 70% |
| 阶段 6A：专家模式与 JSONL | 已完成 | 70% |
| 阶段 6B：会议模式与摘要 | 已完成 | 65% |
| 阶段 6C：工作模式与故障恢复 | 已完成 | 65% |
| 阶段 7：CLI/Client SDK 与参考宿主 | 已完成 | 72% |

## 构建与测试

在 `Src/Madorin.Ai.Runtime` 目录执行：

```powershell
dotnet build .\Madorin.AI.Runtime.slnx -c Debug
dotnet build .\Madorin.AI.Runtime.slnx -c Release
dotnet test .\Madorin.AI.Runtime.slnx
```

## 协议版本

当前 V1 协议版本为 `1.0`，数据库 Schema 版本为 `1`。
