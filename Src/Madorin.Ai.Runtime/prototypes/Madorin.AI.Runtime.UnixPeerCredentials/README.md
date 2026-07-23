# Unix peer credential 独立原型

本项目只验证阶段 2 的 Unix peer credential 可行性，不被生产传输项目引用。它通过两个真实进程建立 Unix domain socket，并在服务端核对内核返回的 PID/UID 与实际子进程。错误用户探针反向引用正式传输项目，用于确认生产 `NamedPipeTransport` 的双端 UID 校验，而不让生产代码依赖原型。

## 平台调用

| 平台 | PID | 用户身份 |
| --- | --- | --- |
| Linux | `getsockopt(SOL_SOCKET, SO_PEERCRED)` | `ucred.uid/gid` |
| macOS | `getsockopt(SOL_LOCAL, LOCAL_PEERPID)` | `getpeereid` |

原型使用 `LibraryImport`，可用于 .NET 10 与 Native AOT。任何原生调用失败都会退出失败，不会退化为跳过 PID 校验。

## 执行

```bash
dotnet run --project ./Madorin.AI.Runtime.UnixPeerCredentials.csproj -c Debug
```

成功输出格式：

```text
PASS platform=linux peerPid=1234 uid=1000 gid=1000 socketMode=600
```

Windows 上只验证可编译性，运行输出 `SKIP`。真实验收必须分别在目标 Unix 环境执行。

## 错误用户探针

先构建 Debug 产物，再以 root 运行编排脚本；脚本默认让 `nobody` 连接 root 创建的 Socket：

```bash
dotnet build ./Madorin.AI.Runtime.UnixPeerCredentials.csproj -c Debug
sudo env \
  DOTNET_BIN="$HOME/.dotnet/dotnet" \
  DOTNET_OWNER_HOME="$HOME" \
  bash ./verify-cross-user.sh
```

正式端点始终创建为 `0600`。负向探针会临时将唯一测试 Socket 放宽到 `0666`，仅用于让错误用户抵达服务端和客户端各自独立的内核 UID 校验；两端均拒绝后立即清理 Socket，并恢复 SDK 所在主目录的原权限。

成功输出同时包含：

```text
PASS platform=linux scenario=wrong-user-client-rejected reason=...
PASS platform=linux scenario=wrong-user-server-rejected reason=...
```

## 发布矩阵

V1 Unix 发布目标全部保留，不用跨平台编译代替实机运行：

```bash
dotnet publish -c Release -r linux-x64 --self-contained
dotnet publish -c Release -r linux-arm64 --self-contained
dotnet publish -c Release -r osx-x64 --self-contained
dotnet publish -c Release -r osx-arm64 --self-contained
```

目标平台 Native AOT 发布后应直接运行对应 `publish/` 下的原生可执行文件，不以交叉编译成功代替实机 `PASS`。

验证结果记录在 [验证记录.md](./验证记录.md)。
