# 阶段 7 批次 3 返工 12：Server 列表与标题编译修复

## 来源会话

- Qwen session：`5852b3aa-2fa5-4e42-be25-462dc58ba862`
- 结果：完成三文件生产编辑，但主代理审查和严格 Debug 构建发现编译错误与标题行为错误。

## 构建证据

`Madorin.AI.Runtime.Server.csproj` 使用 `-c Debug --no-restore -warnaserror -m:1` 构建失败，0 个警告、3 个错误：

- `RuntimeServer.cs(1327,23)`：`RuntimeMode?` 不能传给 `Enum.IsDefined<TEnum>(TEnum)`。
- `RuntimeServer.cs(1327,59)`：`SessionStatus?` 不能传给 `Enum.IsDefined<TEnum>(TEnum)`。
- `RuntimeServer.cs(1365,48)`：`List<SessionListItem>` 不能传给 `SessionListItem[]`。

## 必须修复

1. nullable 枚举只在有值时校验：`Mode` 和 `Status` 为 `null` 时合法，有值且未定义时返回 `-32602`。
2. `SessionListResult.Sessions` 必须传入 `SessionListItem[]`，不要传 `List<SessionListItem>`。
3. 下一页 cursor 的时间必须使用 `CultureInfo.InvariantCulture` 和 `"O"` 格式，与仓储解析保持一致。
4. 标题必须只取 `InitialInput` 中首个非空白 `TextContentBlock`，不得拼接全部文本块。
5. 没有文本块或全部为空白时不得调用无种子的 `Aggregate`，不得抛异常，也不得写标题。
6. 保留 `FlattenSessionTitle` 的 Unicode 空白压平、首尾无空白、120 UTF-16 code unit 和高代理项保护行为。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/RuntimeServer.cs`

不得修改其他文件，不得运行构建或测试，不得使用子代理。只做必要的局部读取和编辑，完成后立即停止。
