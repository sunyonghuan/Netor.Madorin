# Bt Native AOT CLI 与技能资源集成 : 100%

## Step 1 方案落盘 : 100%
- [√] 明确 CLI 定位为单服务器、单次执行、无本地配置的宝塔执行器
- [√] 明确服务器矩阵由 AI/技能层逐台编排，CLI 不保存服务器清单或密钥
- [√] 明确 Native AOT、JSON 源生成和高风险操作确认约束

## Step 2 新增 CLI 项目 : 100%
- [√] 在 `Plugins/Src/Cortana.Plugins.Bt.Cli` 新增 `net10.0` 可执行项目
- [√] 启用 `PublishAot`、`IsAotCompatible`、trim/AOT 友好 JSON 序列化
- [√] 将项目加入 `Plugins/Cortana.Plugins.slnx`

## Step 3 实现宝塔命令 : 100%
- [√] 实现无配置参数解析，支持 `--panel-url`、`--api-sk` 与环境变量
- [√] 实现宝塔 API 签名、表单 POST 和统一 JSON 输出
- [√] 覆盖插件已有系统查询、站点查询、站点配置与站点管理能力
- [√] 对删除、停用、覆盖配置等高风险操作要求 `--yes`
- [√] 提供 `call` 通用命令作为接口兜底

## Step 4 技能与资源 : 100%
- [√] 更新 `skills/bt-cli/SKILL.md`，描述 CLI 调用规范和矩阵编排方式
- [√] 更新 `skills/bt-cli/agents/openai.yaml`
- [√] 将发布后的 CLI 复制到 `skills/bt-cli/assets/win-x64`

## Step 5 验证 : 100%
- [√] `dotnet build` 验证 CLI 项目
- [√] `dotnet publish` 生成 Native AOT 单文件产物
- [√] 运行 `--help` 与参数错误 smoke test
- [√] 运行 skill validator 验证技能格式

## Step 6 长字段输入增强 : 100%
- [√] 为通用 `call` 增加 `--field-file key=path`
- [√] 为通用 `call` 增加单字段 `--field-stdin key`
- [√] 更新帮助文本与技能说明
- [√] 重新构建、发布并验证技能资源中的 AOT CLI

## 修改文件
- `Docs/执行计划(BtCli).md`
- `Plugins/Cortana.Plugins.slnx`
- `Plugins/Src/Cortana.Plugins.Bt.Cli/Cortana.Plugins.Bt.Cli.csproj`
- `Plugins/Src/Cortana.Plugins.Bt.Cli/Program.cs`
- `Plugins/Src/Cortana.Plugins.Bt.Cli/HelpText.cs`
- `Plugins/Src/Cortana.Plugins.Bt.Cli/BtApiClient.cs`
- `Plugins/Src/Cortana.Plugins.Bt.Cli/BtCommandExecutor.cs`
- `Plugins/Src/Cortana.Plugins.Bt.Cli/BtRequestSigner.cs`
- `Plugins/Src/Cortana.Plugins.Bt.Cli/CommandLine/CommandLineParser.cs`
- `Plugins/Src/Cortana.Plugins.Bt.Cli/CommandLine/ParsedArguments.cs`
- `Plugins/Src/Cortana.Plugins.Bt.Cli/Models/AddSiteWebName.cs`
- `Plugins/Src/Cortana.Plugins.Bt.Cli/Models/BtApiResult.cs`
- `Plugins/Src/Cortana.Plugins.Bt.Cli/Models/CliResult.cs`
- `Plugins/Src/Cortana.Plugins.Bt.Cli/Serialization/BtCliJsonContext.cs`
- `skills/bt-cli/SKILL.md`
- `skills/bt-cli/agents/openai.yaml`
- `skills/bt-cli/assets/win-x64/bt-cli.exe`
