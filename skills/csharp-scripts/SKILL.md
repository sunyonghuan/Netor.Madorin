---
name: csharp-scripts
version: 1.0.3
description: 以脚本方式运行单文件 C# 程序，用于快速实验、原型验证和概念测试。当用户希望编写并执行一个小型 C# 程序而无需创建完整项目时使用。
---

# C# 脚本

## 什么是「脚本」

本技能处理以**脚本形态**运行的 C# 代码：

- 顶级语句（无 `Main` 方法、无 `class Program` 包装）
- 单个表达式
- `.csx` 文件
- 不含显式入口方法的 `.cs` 文件

**含 `Main` / `class Program` 的完整控制台程序交给标准项目工作流**（`dotnet new console` + `dotnet run`），不走本技能。

> **不要为了"验证脚本形态"去读取脚本文件内容。** 是不是合法脚本由运行时判断：执行成功即合法；失败即报错——把错误原样透给用户，不要预读内容浪费 token。

## 触发条件

满足任一条件即启用：

- 用户给出一段顶级语句形式的 C# 代码并要求「运行 / 执行 / 跑一下 / 看结果」
- 用户希望用 C# 完成一次性计算、API 探索、数据转换或语法检查
- 用户给出 `.csx` 或 `.cs` 文件路径并要求执行（**直接执行，不预读文件内容**）

## 脚本执行方式

有两条独立的执行路径，互不相关：

### 方式 A：调用 `csx_script` 工具（首选）

`csx_script` 是宿主上**已经安装好的进程插件**（打包为 exe），对外暴露若干工具；调用这些工具即可执行脚本，不需要本机装 .NET SDK，只需要 Runtime。

| 代码形态 | 调用的工具 |
|----------|-----------|
| 单个表达式（无分号、无语句） | `csx_eval` |
| 多行脚本代码在内存里（字符串） | `csx_run_str` |
| **用户给出文件路径** / 代码 > 256 K 字符 / 需要 `#load` | `csx_run_file`（直接传路径，不读文件） |
| 多步任务需要跨调用共享变量、`using`、函数 | `csx_session_create` → `csx_session_exec` × N → `csx_session_close` |
| 用户明确要求「只检查不执行」 | `csx_check` |
| 用户明确要求格式化代码 | `csx_format` |

约束：

- 脚本里直接使用全局对象 `Log` / `Settings` / `Host`，不要 `using`
- 引入 NuGet 包用 `#r "nuget:Pkg, 1.2.3"`；若 `get_info.instructions` 提示宿主未装 SDK，改用不依赖该包的实现或改走方式 B
- 单行协议帧 ≤ 4 MB，code ≤ 256 K 字符，超出必须走 `csx_run_file`

### 方式 B：本机 `dotnet` 命令（回退）

仅当 `csx_script` 工具不可用时使用：

- `dotnet --version` ≥ 10 → `dotnet <file>.cs`（`.cs` 必须是顶级语句形式）
- 版本低于 10 → 用临时控制台项目（见下文「.NET 9 及更早版本的回退方案」）

引入 NuGet 包用 `#:package Pkg@1.2.3`（这是 CLI 专有指令，不要写在要交给 `csx_script` 的脚本里）。

## 执行决策

按顺序判断，命中即停：

1. `csx_script` 工具是否可用？是 → 用方式 A
2. 否 → 用方式 B

**禁止**跳过第 1 步直接走方式 B。

### 输入为文件路径时

直接把路径传给 `csx_run_file`（方式 A）或 `dotnet <file>`（方式 B），**不要先读文件内容**。文件是否是合法脚本由工具判断——成功就是成功，失败把报错返给用户即可。

只有当用户要求「修改 / 解释 / 调试」脚本时才读取内容；**单纯执行不读**。

### 输入为代码片段时

代码已经在上下文里，按「什么是脚本」判断形态，选择合适的工具：单表达式用 `csx_eval`，多行用 `csx_run_str`，超长（>256K 字符）落盘后用 `csx_run_file`。

## 扩展名速查

| 扩展名 | 方式 A（`csx_run_file`） | 方式 B（`dotnet <file>`） |
|--------|--------------------------|---------------------------|
| `.csx` | ✅ 原生支持 | ❌ 不支持 |
| `.cs`（顶级语句） | ✅ 按 CSX 语义执行 | ✅ |
| `.cs`（含 `Main`） | ⚠️ 交由工具判定，不预读 | —（项目工作流） |
| 其它 | ❌ | ❌ |

---

## 适用场景

- 用单文件程序快速测试 C# 概念、API 或语言特性
- 在集成到大型项目之前进行逻辑原型验证

## 不适用场景

- 用户需要包含多个文件或项目引用的完整项目
- 用户正在已有的 .NET 解决方案中工作，希望在其中添加代码
- 程序过于庞大或复杂，不适合放在单个文件中

## 输入

| 输入 | 是否必需 | 说明 |
|------|----------|------|
| C# 代码或意图描述 | 是 | 要运行的代码，或对脚本功能的描述 |

## 方式 B 工作流程（本机 dotnet CLI）

### 步骤 1：检查 .NET SDK 版本

运行 `dotnet --version` 确认 SDK 已安装，并记录主版本号。基于文件的应用需要 .NET 10 或更高版本。如果版本低于 10，请改用[旧版 SDK 回退方案](#net-9-及更早版本的回退方案)。

### 步骤 2：编写脚本文件

使用顶级语句创建单个 `.cs` 文件。将文件放在任何现有项目目录之外，以避免与 `.csproj` 文件冲突。

```csharp
// hello.cs
Console.WriteLine("Hello from a C# script!");

var numbers = new[] { 1, 2, 3, 4, 5 };
Console.WriteLine($"Sum: {numbers.Sum()}");
```

编写指南：

- 使用顶级语句（无需 `Main` 方法、类或命名空间样板代码）
- 将 `using` 指令放在文件顶部
- 将类型声明（类、记录、枚举）放在所有顶级语句之后

### 步骤 3：运行脚本

```bash
dotnet hello.cs
```

自动构建并运行文件。已缓存，后续运行速度很快。在 `--` 后传递参数：

```bash
dotnet hello.cs -- arg1 arg2 "multi word arg"
```

### 步骤 4：添加 NuGet 包（如需要）

在文件顶部使用 `#:package` 指令引用 NuGet 包。务必指定版本号：

```csharp
#:package Humanizer@2.14.1

using Humanizer;

Console.WriteLine("hello world".Titleize());
```

### 步骤 5：清理（**视情况**）

脚本分两类，处理方式不同：

| 类型 | 判定依据 | 执行完的处理 |
|------|----------|----------------|
| **临时脚本** | 用户说「测试一下 / 跑一下 / 验证一下」；AI 为演示或一次性计算临时写的文件；放在 `tmp/` `temp/` `%TEMP%` 等临时目录 | 删除文件，必要时 `dotnet clean <file>.cs` |
| **长期脚本** | 用户明确保存到某个目录；已存在于项目 / 仓库 / `scripts/` `tools/` 等目录；用户提及「以后还要用 / 保留 / 加到 git」 | **不删除**，仅在用户明确要求时执行清理 |

默认原则：**不确定时默认保留，并提醒用户文件路径**。删除动作不可逆，宁愧勿纵。

```bash
# 仅在临时脚本时执行：
dotnet clean hello.cs
Remove-Item hello.cs
```

## Unix shebang 支持

在 Unix 平台上，可以让 `.cs` 文件直接可执行：

1. 在文件第一行添加 shebang：

    ```csharp
    #!/usr/bin/env dotnet
    Console.WriteLine("I'm executable!");
    ```

2. 设置执行权限：

    ```bash
    chmod +x hello.cs
    ```

3. 直接运行：

    ```bash
    ./hello.cs
    ```

添加 shebang 时使用 `LF` 换行符（非 `CRLF`）。此指令在 Windows 上会被忽略。

## 源代码生成的 JSON

基于文件的应用默认启用原生 AOT。基于反射的 API（如 `JsonSerializer.Serialize<T>(value)`）在 AOT 下运行时会失败。请改用源代码生成的序列化：

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

var person = new Person("Alice", 30);
var json = JsonSerializer.Serialize(person, AppJsonContext.Default.Person);
Console.WriteLine(json);

var deserialized = JsonSerializer.Deserialize(json, AppJsonContext.Default.Person);
Console.WriteLine($"Name: {deserialized!.Name}, Age: {deserialized.Age}");

record Person(string Name, int Age);

[JsonSerializable(typeof(Person))]
partial class AppJsonContext : JsonSerializerContext;
```

## 转换为项目

当脚本超出单文件的承载能力时，将其转换为完整项目：

```bash
dotnet project convert hello.cs
```

## .NET 9 及更早版本的回退方案

如果 .NET SDK 版本低于 10，则基于文件的应用不可用。请改用临时控制台项目：

```bash
mkdir -p /tmp/csharp-script && cd /tmp/csharp-script
dotnet new console -o . --force
```

将生成的 `Program.cs` 替换为脚本内容，然后使用 `dotnet run` 运行。使用 `dotnet add package <name>` 添加 NuGet 包。临时项目执行完后可删除该目录；若用户要求保留，保留即可。

## 验证清单

- [ ] `dotnet --version` 报告 10.0 或更高版本（或已使用回退方案）
- [ ] 脚本编译无错误（可通过 `dotnet build <file>.cs` 显式检查）
- [ ] `dotnet <file>.cs` 产生预期输出
- [ ] 仅对「临时脚本」执行了清理；「长期脚本」保留未被误删

## 常见问题

| 问题 | 解决方案 |
|------|----------|
| `.cs` 文件位于包含 `.csproj` 的目录中 | 将脚本移到项目目录之外，或使用 `dotnet run --file file.cs` |
| `#:package` 未指定版本 | 指定版本：`#:package PackageName@1.2.3` 或 `@*` 表示最新版 |
| 基于反射的 JSON 序列化失败 | 使用源代码生成的 JSON 和 `JsonSerializerContext`（参见[源代码生成的 JSON](#源代码生成的-json)） |
| 意外的构建行为或版本错误 | 基于文件的应用会继承父目录中的 `global.json`、`Directory.Build.props`、`Directory.Build.targets` 和 `nuget.config`。如果继承的设置冲突，请将脚本移到隔离目录 |

## 更多信息

参阅 https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps 获取基于文件的应用的完整参考。
