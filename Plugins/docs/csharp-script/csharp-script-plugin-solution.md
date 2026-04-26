# C# Script Runner 插件方案（定稿）

> 插件 ID：`sys_csx_script`  ·  显示名：`C# Script Runner`
> 运行模式：`runtime: process`（框架依赖发布的普通 .NET 10 exe）
> 仓库位置：`Plugins/Src/Cortana.Plugins.CSharpScript/`

---

## 一、定位与核心决策

让用户**无需安装 .NET SDK，仅安装 .NET Runtime** 即可执行 C# 脚本（`.csx`）。插件本身是一个普通的 .NET 10 exe，以子进程形式由 Cortana 主进程按 `runtime: process` 契约启动，通过 stdio JSON-RPC 通信。

### 为什么不能用 Native AOT

Roslyn Scripting（`Microsoft.CodeAnalysis.CSharp.Scripting`）在运行时使用 `System.Reflection.Emit` 动态生成 IL，与 AOT/Trimming 根本冲突。按仓库 AOT 风险评估规则，**此插件必须使用 JIT**，框架依赖发布（`--self-contained false`），由用户机器提供 Runtime。

### 为什么不做 Runtime 检测 / 安装引导

- Runtime 检测走智能体命令（`dotnet --list-runtimes`）完成，不是插件职责
- 未安装时子进程启动会失败，框架捕获错误即可
- 插件本身不承担任何安装引导 UI

### 为什么不连 WebSocket / 不调用其他插件

- 宿主调用工具时天然返回结果给 AI，再回连 WS 是多此一举
- 插件间必须相互独立，`ScriptGlobals` 不暴露跨插件调用

---

## 二、项目结构

```
Plugins/Src/Cortana.Plugins.CSharpScript/
├─ Cortana.Plugins.CSharpScript.csproj
├─ plugin.json                  # runtime: process，command: csharp-script.exe
├─ Program.cs                   # stdio JSON-RPC 消息循环
├─ Protocol/                    # NativeHostRequest/Response DTO（从框架拷贝或自写）
│  └─ HostProtocol.cs
├─ Runner/
│  ├─ ScriptRunner.cs           # CSharpScript 封装
│  ├─ ScriptSession.cs          # 会话管理（M2）
│  └─ ScriptOptionsFactory.cs   # 预设程序集/命名空间
├─ Globals/
│  ├─ ScriptGlobals.cs          # 暴露给脚本的根对象
│  ├─ ISettingsBridge.cs
│  └─ IScriptHost.cs
├─ Tools/                       # 每个 Tool 一个文件
│  ├─ RunStrTool.cs
│  ├─ RunFileTool.cs
│  └─ ...
└─ README.md
```

### csproj 关键配置

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <OutputType>Exe</OutputType>
  <AssemblyName>csharp-script</AssemblyName>
  <Version>1.0.0</Version>
  <ImplicitUsings>enable</ImplicitUsings>
  <Nullable>enable</Nullable>
  <!-- 不设 PublishAot，不设 SelfContained -->
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Scripting" Version="4.*" />
  <PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.*" />
</ItemGroup>
```

### 发布命令

```powershell
dotnet publish Plugins/Src/Cortana.Plugins.CSharpScript -c Release -r win-x64 --self-contained false
```

产物体积：`csharp-script.exe` + Roslyn DLL ≈ 30–40 MB。

---

## 三、stdio JSON-RPC 契约

与框架对齐（见 `Src/Netor.Cortana.Plugin/Native/NativeHostProtocol.cs`）。**每行一个 JSON**，经由 stdin/stdout。

### 请求（宿主 → 插件）

```json
{ "method": "get_info|init|invoke|destroy", "toolName": "...", "args": "<json-string>" }
```

### 响应（插件 → 宿主）

```json
{ "success": true,  "data":  "<json-string-or-text>" }
{ "success": false, "error": "..." }
```

### 需处理的 method

| method | 负载 |
|---|---|
| `get_info` | 返回 `NativePluginInfo`（id/name/version/description/tags/instructions + tools[]） |
| `init` | 接受 `NativePluginInitConfig`（`DataDirectory` / `WorkspaceDirectory` / `WsPort` / `PluginDirectory`），新建 `ScriptRunner` |
| `invoke` | `toolName` + `args`（JSON 字符串）→ 派发到具体 Tool 处理器 |
| `destroy` | 清理资源并退出 |

### 循环伪代码

```csharp
var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
var reader = new StreamReader(stdin, Encoding.UTF8);
var writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = false };

while (await reader.ReadLineAsync() is { } line)
{
    var req = JsonSerializer.Deserialize<HostRequest>(line);
    var resp = await Dispatcher.HandleAsync(req);
    await writer.WriteLineAsync(JsonSerializer.Serialize(resp));
    await writer.FlushAsync();
    if (req.Method == "destroy") break;
}
```

### 错误处理

- `invoke` 任何异常 → 捕获后返回 `{success:false, error}`，进程不终止
- JSON 解析失败 → 同上
- stdout 只允许写协议 JSON；脚本自己的 `Console.WriteLine` 需重定向到捕获缓冲区（下节）
- 诊断/日志 → 写 stderr，框架自动记日志

---

## 四、Tool 清单（12 个）

所有 Tool 以 `sys_csx_` 为前缀。

### 执行类（3）

| Tool | 参数 | 说明 |
|---|---|---|
| `sys_csx_run_str` | `code: string, timeoutMs?: int` | 执行 CSX 源码字符串 |
| `sys_csx_run_file` | `path: string, timeoutMs?: int` | 执行 `.csx` 文件，支持 `#load` 相对路径 |
| `sys_csx_eval` | `expr: string` | 单表达式求值 |

### 会话类（4）

同一 session 内变量/函数/类定义均保留。

| Tool | 参数 | 说明 |
|---|---|---|
| `sys_csx_session_create` | `initialCode?: string` | 创建会话，返回 `sessionId` |
| `sys_csx_session_exec` | `sessionId, code, timeoutMs?` | 在 session 内续执 |
| `sys_csx_session_reset` | `sessionId` | 清空状态，保留 id |
| `sys_csx_session_close` | `sessionId` | 关闭会话 |

### 诊断类（2）

| Tool | 参数 | 说明 |
|---|---|---|
| `sys_csx_check` | `code: string` | 只做编译检查，不执行 |
| `sys_csx_format` | `code: string` | Roslyn 格式化 |

### 依赖类（2）

| Tool | 参数 | 说明 |
|---|---|---|
| `sys_csx_add_reference` | `sessionId, package, version` | 当前 session 追加 NuGet 引用 |
| `sys_csx_list_references` | `sessionId` | 列当前程序集 |

### 能力发现（1）

| Tool | 参数 | 说明 |
|---|---|---|
| `sys_csx_get_globals_api` | 无 | 返回 `ScriptGlobals` 的 API 清单，让 AI 读懂脚本上下文 |

### 统一返回结构

所有执行类/会话类 Tool 的 `data` 统一如下（再装入外层 `{success, data}`）：

```json
{
  "sessionId": "xxx",
  "returnValue": "...",
  "returnType":  "System.Int32",
  "stdout": "...",
  "stderr": "...",
  "diagnostics": [
    { "severity": "Error", "line": 3, "column": 10, "message": "CS1002: ; expected" }
  ],
  "elapsedMs": 42
}
```

---

## 五、ScriptGlobals（脚本全局对象）

```csharp
public sealed class ScriptGlobals
{
    public ILogger Log { get; }               // 写插件日志目录
    public ISettingsBridge Settings { get; }  // 读/写本插件 KV（仅自己）
    public IScriptHost Host { get; }          // 插件信息 / 目录 / CancellationToken
}

public interface ISettingsBridge
{
    string? Get(string key);
    void Set(string key, string value);
    IReadOnlyDictionary<string, string> Snapshot();
}

public interface IScriptHost
{
    string PluginDirectory { get; }
    string DataDirectory { get; }
    string WorkspaceDirectory { get; }
    CancellationToken Cancellation { get; }
}
```

脚本里直接写：

```csharp
Log.LogInformation("hello");
var token = Settings.Get("api_key");
return DateTime.Now;
```

**无** `Dialog`，**无** `Plugins`。返回值经工具响应回给宿主 → AI 自行处理。

---

## 六、脚本能力矩阵

| 特性 | 支持 | 备注 |
|---|---|---|
| Top-level statements | ✅ | Scripting 天然支持 |
| `async/await` | ✅ | `CSharpScript.RunAsync` |
| 类 / record / 泛型 | ✅ | |
| LINQ / pattern matching / C# 13+ | ✅ | 跟 Roslyn 版本走 |
| `#r "nuget: xxx, ver"` | ✅ M3 | 需自实现 MetadataReferenceResolver |
| `#load "helper.csx"` | ✅ | `SourceFileResolver` |
| 反射 / `dynamic` | ✅ | |
| `unsafe` / 指针 | ⚠ 默认关 | 设置项开启 |
| `namespace` 定义 | ❌ | Scripting 限制 |
| P/Invoke | ✅ | |
| 多文件工程 | ⚠ | 靠 `#load` 套套，完全工程化需走文档八章的元能力 |

---

## 七、安全边界

| 项 | 默认 | 说明 |
|---|---|---|
| 执行超时 | 30 s | `timeoutMs` 覆盖，`CancellationTokenSource` |
| 内存限制 | 不设 | 进程隔离已足够，崩溃只死子进程 |
| `unsafe` | 关 | 设置项开启 |
| 文件 / 网络 | 不拦截 | 首版信任用户 |
| NuGet 源 | nuget.org | 后续支持白名单 |
| stdout 污染 | 拦截 | 脚本 `Console.Write*` 重定向至专用缓冲区，不流到宏观 stdout |

---

## 八、元能力：插件脚手架生成（M4，先不实现）

### 8.1 定位澄清

- **不是把单文件 CSX 变插件**（那价值不大，脚本本来就能执行）
- **是由本插件生成工程化的 CSX 插件脚手架**
- AI 基于脚手架填充业务代码，产物是独立的 `runtime: process` 插件项目，可常驻、可承担重型任务
- 本插件不负责编译/发布，只生成骨架 + 提取清单 + 输出提示词

### 8.2 元能力 Tool（待实现）

| Tool | 职责 |
|---|---|
| `sys_csx_scaffold_plugin` | 按 `{targetDir, id, name, version}` 在目标目录生成完整 CSX 插件工程：csproj + Program.cs（内置 stdio 消息循环）+ Tools/ + README |
| `sys_csx_extract_manifest` | 扫描指定项目源码中的 `[Plugin]`/`[Tool]`/`[Parameter]` 标注，生成 `plugin.json` 写入项目根目录 |
| `sys_csx_get_build_prompt` | 返回一段提示词，包含 `dotnet publish` 命令、产物验证清单、zip 打包命令，由 AI 通过 shell 执行 |
| `sys_csx_get_dev_guide` | 返回开发指南：消息循环契约、Tool 标注规范、调试技巧，供 AI 阅读后写业务 |

### 8.3 脚手架产物模板

```
<targetDir>/<ProjectName>/
├─ <ProjectName>.csproj       # net10.0, Exe, 非 AOT
├─ Program.cs                 # stdio 消息循环模板（一字不改）
├─ Tools/
│  └─ HelloTool.cs           # 示例 Tool，AI 可替换
├─ PluginInfo.cs              # [Plugin] 标注 + Tool 注册表
└─ README.md                  # 构建步骤 + 契约说明
```

### 8.4 与现有发布流程衔接

- `Plugins/publish.ps1` 原封不动，AI 按提示词直接调
- 编译命令与其他 process 模式插件一致：
  ```powershell
  dotnet publish -c Release -r win-x64 --self-contained false
  ```

---

## 九、交付路线

| 阶段 | 内容 | 状态 |
|---|---|---|
| **M0** | stdio 消息循环与契约骨架 + `sys_csx_run_str` 跑通 `1+1` + `get_info` 返回正确 | 进行中 |
| M1 | 执行类 3 个 Tool + 诊断类 2 个 + `ScriptGlobals`（Log + Host + Settings） + 超时 | 待办 |
| M2 | 会话类 4 个 Tool + stdout 捕获隔离 + 诊断串行化 | 待办 |
| M3 | `#r "nuget:"` 支持 + 能力发现 Tool + 设置面板 | 待办 |
| M4 | 元能力（脚手架生成 + 清单提取 + 构建提示词） | 待办 |

---

## 十、定稿确认点

- ✅ 插件 ID `sys_csx_script`，显示名 `C# Script Runner`，Tool 前缀 `sys_csx_*`
- ✅ 12 个 Tool 清单（执行 3 + 会话 4 + 诊断 2 + 依赖 2 + 能力发现 1）
- ✅ ScriptGlobals 只有 Log / Settings / Host，不连 WS、不跨插件
- ✅ Native AOT 风险评估通过：必须用 process 模式 + JIT
- ✅ Runtime 检测走智能体命令，插件不包
- ✅ 元能力 = 脚手架生成器（不是运行时注册）
- ✅ 编译/发布由 AI 调命令完成，本插件不调 SDK

