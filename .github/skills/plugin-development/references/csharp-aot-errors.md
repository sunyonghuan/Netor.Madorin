---
title: C# AOT 编译错误速查
version: 1
---

# C# AOT 编译错误速查

## Native Generator 错误（CNPGxxx）

| 错误码 | 原因 | 解决 |
|---|---|---|
| `CNPG003` | 工具参数类型不支持 | 改为 string / int / long / double / float / decimal / bool |
| `CNPG004` | 工具名冲突 | 用 `[Tool(Name = "...")]` 改名 |
| `CNPG005` | 工具类不是 public | 加 `public` |
| `CNPG006` | 工具方法不是 public | 加 `public` |
| `CNPG009` | `[Plugin]` 缺 Id 或 Name | 补齐 |
| `CNPG010` | 项目里有多个 `[Plugin]` 类 | 只保留一个 |
| `CNPG011` | Startup 不是 `public static partial` | 三个修饰符都加上 |
| `CNPG012` | 缺 `Configure(IServiceCollection)` | 添加该方法 |
| `CNPG019` | Id 格式非法 | 只用小写字母、数字、下划线，字母开头 |
| `CNPG020` | 返回自定义类型但没有 `PluginJsonContext` | 创建 `PluginJsonContext` 派生类 |

## AOT / 发布错误

| 症状 | 原因 | 解决 |
|---|---|---|
| `NETSDK1099` 链接器找不到 | 未装 C++ 构建工具 | 运行 [setup-dev-environment.ps1](../scripts/setup-dev-environment.ps1) |
| `PublishSingleFile` + AOT 冲突 | AOT 不兼容 SelfContained=false 单文件 | 删 csproj 的 `<SelfContained>` 和 `<PublishSingleFile>` |
| 反射/动态代码警告 `IL2026/IL3050` | AOT 不支持的 API | 避开反射，或换 Source Generator 方案 |
| 运行时找不到入口点 | 发布模式不是 AOT | 确认 `<PublishAot>true</PublishAot>` |

## Generator 代码不更新

```powershell
dotnet clean; dotnet build
```

仍无效：删除 `bin/` 和 `obj/` 后重建。
