# 阶段 7 批次 3 返工 9：Qwen 多行提示传参

## 失败会话

- Qwen session：`4e15fa31-59d2-4647-b29c-cd4a07f69ae4`
- 结果：Qwen 仅收到提示词第一行，未读取任务文档，未调用编辑工具，也未修改文件。
- 原因：PowerShell 调用 `qwen.cmd` 时，传给 `--prompt` 的多行变量未显式引用，原生命令参数绑定截断了提示内容。

## 新会话要求

- 不使用 `--resume`，启动全新 Qwen 会话。
- 使用显式引用的单个 `--prompt` 参数传递完整多行提示。
- 首先读取 `阶段7-批次3-返工8-Qwen生产实现工具预算.md`，然后直接执行局部编辑。
- 禁止整文件读取、glob、递归搜索、搜索 Contracts/Entities、运行构建或测试、使用子代理。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/RuntimeServer.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/IRuntimeClient.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeClient.cs`

除上述三个文件外，不得修改任何代码或文档。完成返工 8 的四项编辑后立即停止。
