# 阶段 7 批次 1 返工 1：Client 公共类型

## 问题

- `RuntimeStartPolicy` 的类型摘要存在 `discovers or launch` 语法错误。
- 稳定 Client 异常模型缺少独立认证异常，无法让宿主区分认证失败与一般协议失败。
- `RuntimeInstanceBinding` 的注释没有明确该快照只在认证和初始化成功后公开。
- 异常类型需保持 inner exception 和可重试标记，不得引入新的依赖或序列化协议。

## 必须修改

- 修正 `RuntimeStartPolicy` 类型摘要。
- 增加 `RuntimeClientAuthenticationException : RuntimeClientException`，提供 message 与 message/innerException 构造函数。
- 保留现有启动、连接、协议、版本不兼容和健康检查异常类型。
- 调整 `RuntimeInstanceBinding` 类型摘要，明确它表示已连接并完成初始化的实例绑定；不改变当前字段顺序和类型。
- 公共类型与公共成员保留简洁 XML 文档。

## 允许修改

- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeStartPolicy.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/OwnedProcessClosePolicy.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeInstanceBinding.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeClientException.cs`

## 禁止修改

- 其他任何源码、测试、项目文件或文档。
- 阶段 6C 当前未提交文件。

## 验收

```powershell
dotnet build .\src\Madorin.AI.Runtime.Client\Madorin.AI.Runtime.Client.csproj -c Debug --no-restore -warnaserror -m:1
```
