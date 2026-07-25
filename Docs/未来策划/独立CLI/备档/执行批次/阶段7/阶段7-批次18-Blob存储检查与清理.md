# 阶段 7 批次 18：Blob 存储检查与清理 : 100%

## 目标与冻结边界

- [√] `storage check` 复用全局 `--workspace` 与 `--data-dir`，只读汇总 Canonical JSONL、SQLite 工具结果和 `blobs/*.blob`，不创建目录、数据库、WAL/SHM、恢复点或锁文件。
- [√] `storage check --verify-hashes` 对有效 Blob 文件流式计算 SHA-256；默认仍检查引用缺失、文件名、元数据冲突和已知长度，不读取全部文件内容。
- [√] JSONL 或 SQLite 扫描不完整时返回明确问题；不得把无法完整证明引用关系的 Blob 归类为可清理孤立文件。
- [√] `storage gc` 参数统一为 `--older-than <days>`、`--dry-run`、`--confirm`；默认龄期 7 天，负数返回退出码 2，`--dry-run` 与 `--confirm` 互斥。
- [√] 未提供 `--confirm` 时默认只展示计划且不获取工作区写锁；实际执行先获取排他锁，再在锁内重扫引用、文件和龄期。
- [√] 实际清理只处理名称为小写 64 位 SHA-256 的未引用 `.blob` 文件；活动引用、过新文件、非法文件名和扫描不完整状态一律保护。
- [√] 删除前建立 `backups/storage-gc-*` 恢复点，将候选 Blob 和清单移入恢复点；中途失败保留恢复点、未移动文件和原始诊断。
- [√] CLI 只调用 Server 高层维护服务，不直接解析 JSONL、打开 SQLite 或删除 Blob。

## 测试与验证

- [√] 红灯覆盖 help/参数、健康检查、缺失引用、孤立 Blob、可选哈希、默认 dry-run、龄期保护、显式确认、锁冲突、恢复点失败和中途故障。
- [√] 定向回归、Persistence、EndToEnd、全解决方案、严格构建、Native AOT、CodeMap 和差异检查通过。

## 允许修改文件

- [√] `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次18-Blob存储检查与清理.md`
- [√] `Docs/未来策划/独立CLI/命令规范/04-CLI命令规范.md`
- [√] `Docs/未来策划/独立CLI/备档/执行步骤/07-CLI-ClientSDK与参考宿主.md`
- [√] `Docs/未来策划/独立CLI/执行步骤/README.md`
- [√] `Docs/未来策划/独立CLI/备档/执行计划/执行计划(阶段7-CLI-ClientSDK与参考宿主).md`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/StorageMaintenanceModels.cs`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/StorageMaintenanceService.cs`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/CliApplication.cs`
- [√] `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/StorageCommands.cs`
- [√] `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneStorageCommandsStage7Tests.cs`

## 验证记录

- 红灯阶段 11 / 11 失败，均命中缺失参数或原有成功 `NotImplemented` 占位；实现后定向测试 11 / 11、Storage 与 Database 联合回归 54 / 54 通过。
- Persistence 171 / 171、EndToEnd 222 / 222、全解决方案 793 项通过；3 个真实 Provider 冒烟按凭据条件跳过。
- Debug/Release 严格构建均为 0 警告、0 错误；Windows `win-x64` Release Native AOT 发布为 0 条 trimming/AOT 警告。
- AOT 制品 `madorin --help`、`madorin storage check --help`、`madorin storage gc --help` 均返回 0；协议保持 `1.1`，Schema 保持 `14`。
- CodeMap workspace `session` 已刷新本批 4 个 C# 文件至 overlay revision `104`；`git diff --check` 通过，仅有既有 LF/CRLF 提示。
