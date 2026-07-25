# 阶段 7 批次 3 返工 5：Qwen 工具预算

## 失败会话

- Qwen session：`e7487ac1-97d4-457d-b1f1-93cd57775268`
- 模型：`qwen3.7-plus`
- 参数：`--safe-mode --approval-mode auto-edit --max-wall-time 3m --max-tool-calls 20 --output-format stream-json`
- 结果：Qwen 在第 21 次工具调用前中止，未执行文件编辑。

## 原因

- 提示要求读取“受影响目录 README”，Qwen 读取了 900 行的总体策划 README，并继续重复检索已有测试帮助方法。
- 工具预算耗尽时仍停留在测试结构审计，没有完成两个局部生产补丁和新测试文件。

## 新会话要求

- 不使用 `--resume`，启动全新 Qwen 会话。
- 只读取本返工文档、`Src/Madorin.Ai.Runtime/README.md`、生产文件目标片段和现有持久化测试的必要帮助方法。
- 直接完成返工 4 的两个局部补丁，并在独立 `SessionListStage7Tests.cs` 中增加两个回归测试。
- 不使用子代理，不读取无关总体策划，不修改白名单之外的文件。
- 允许适度提高工具调用预算，但完成编辑后立即停止，由主代理负责构建和测试。
