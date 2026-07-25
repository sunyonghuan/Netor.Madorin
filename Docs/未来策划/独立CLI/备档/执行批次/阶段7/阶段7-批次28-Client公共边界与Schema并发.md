# 阶段 7 批次 28：Client 公共边界与 Schema 并发 : 100%

## 目标与结论

- [√] 将 `RuntimeClient.ControlPeer` 和 `RuntimeClient.BlobChannel` 从公共表面收口为内部测试能力，宿主不再接触 Named Pipe 帧或底层传输对象。
- [√] 增加程序集级公共 API 边界测试，扫描 Client 程序集全部导出签名并禁止暴露 `Madorin.AI.Runtime.Transport.*` 类型。
- [√] 修复 JsonSchema.Net 默认全局 `SchemaRegistry` 在并行构建 Schema 时的第三方非线程安全共享状态，每个缓存条目使用独立 registry。
- [√] 本批不修改协议 DTO、协议版本或数据库 Schema。

## 实施证据

- [√] `RuntimeClient` 的底层控制 Peer 和 Blob 通道属性改为 `internal`；生产项目和 SampleHost 均无引用，EndToEnd 测试通过既有 `InternalsVisibleTo` 使用测试能力。
- [√] `ClientAssembly_PublicSurface_DoesNotExposeTransportTypes` 检查导出类型的构造器、方法、属性、字段、事件、基类和接口，并显式断言两个底层属性不是公共属性。
- [√] 公共表面反射测试只对其不可避免的 trimming 分析告警使用带理由的局部 `UnconditionalSuppressMessage`，未关闭项目级分析。
- [√] `JsonSchemaToolValidator` 使用 `BuildOptions { SchemaRegistry = new SchemaRegistry() }` 构建 Schema，继续按 Schema JSON 缓存已构建对象。
- [√] `SchemaValidator_ConcurrentInstances_UseIsolatedSchemaRegistries` 并行创建 128 个 Validator 和带独立 `$id` 的输入/输出 Schema，全部验证成功。

## 测试与门禁

- [√] Client 公共 API 边界定向测试 1 / 1 通过。
- [√] Schema registry 并发回归测试 1 / 1 通过。
- [√] `Madorin.AI.Runtime.EndToEnd.Tests` 254 / 254 通过。
- [√] `Madorin.AI.Runtime.Provider.Tests` 131 项通过，3 个真实 Provider 用例因缺少外部凭据跳过。
- [√] 全解决方案 831 项通过，3 个真实 Provider 用例因缺少外部凭据跳过，0 失败。
- [√] Debug/Release `--no-restore -warnaserror -m:1` 均为 0 警告、0 错误。
- [√] `git diff --check` 通过，仅有工作树既有的 LF/CRLF 转换提示。
- [√] 协议保持 `1.1`，SQLite Schema 保持 `14`；批次 25 的 Windows `win-x64` Release Native AOT 无警告证据继续有效。
- [√] CodeMap workspace `session` 刷新 4 个 C# 文件、121 个符号，overlay revision 为 `164`。

## 进度与剩余边界

- [√] 07 完成标准由 6 / 8 更新为 7 / 8，总清单由 89 / 96 更新为 90 / 96（93.8%）。
- [√] 执行步骤总览由 762 / 912 更新为 763 / 912（83.7%）。
- [√] 阶段 7 执行计划保持 46 / 54（85.2%）；本批关闭的主清单完成标准不对应执行计划中的独立未完成项。
- [×] 剩余 6 项继续保留：Client 生命周期联合矩阵、完整多进程拓扑、同 ID 多实例隔离、首次启动与发布联合验证、双 CLI 进程矩阵，以及新终端/PATH/双击实测。

## 修改文件

- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次28-Client公共边界与Schema并发.md`
- `Docs/未来策划/独立CLI/备档/执行步骤/07-CLI-ClientSDK与参考宿主.md`
- `Docs/未来策划/独立CLI/执行步骤/README.md`
- `Docs/未来策划/独立CLI/备档/执行计划/执行计划(阶段7-CLI-ClientSDK与参考宿主).md`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeClient.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Services/Tools/JsonSchemaToolValidator.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/RuntimeClientLifecycleStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Provider.Tests/ToolCatalogStoreTests.cs`
