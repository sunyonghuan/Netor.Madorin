---
name: fix-key-permission
description: Fix SSH key file permission issues on Windows
license: MIT
user-invocable: false
---

# Fix Key Permission（修复密钥权限）

## 功能描述

在 Windows 系统下，SSH 密钥文件（id_rsa）的权限设置过于开放会导致 OpenSSH 拒绝使用，出现 "Permission denied" 或 "invalid format" 错误。此子技能用于修复密钥文件权限，使其符合 OpenSSH 的安全要求。

## 问题场景

当用户遇到以下错误时调用此子技能：
- `Load key "xxx\id_rsa": Permission denied`
- `Load key "xxx\id_rsa": invalid format`
- `Permissions too open`
- 密钥登录失败，回退到密码认证

## 调用触发

当用户请求：
- "密钥权限问题"
- "Permission denied"
- "密钥无法使用"
- "修复密钥权限"
- 连接服务器时出现密钥权限错误

**主技能将自动调用此子技能执行权限修复。**

## 解决方案

### 步骤 1：断开继承权限（手动执行）

```powershell
icacls "{工作目录}\Servers\{服务器 IP}\id_rsa" /inheritance:r
```

**参数说明**：
- `/inheritance:r` - 移除继承的权限，防止父目录权限影响

### 步骤 2：使用脚本设置 Administrator 完全控制权限

**执行脚本文件**：
```powershell
powershell.exe -ExecutionPolicy Bypass -File "E:\Workspace\.madorin\skills\server-maintainer\scripts\fix-key-permission.ps1"
```

**或者在会话中直接运行脚本内容**：
```powershell
# 1. 定义文件路径（替换 {ip地址} 为实际服务器 IP）
$FilePath = "{工作目录}\Servers\{ip地址}\id_rsa"

# 2. 自动获取当前机器的 Administrator 账户的 SID
$AdminSID = (New-Object System.Security.Principal.NTAccount("Administrator")).Translate([System.Security.Principal.SecurityIdentifier]).Value

# 3. 断开继承权限 (清除旧权限)
icacls "$FilePath" /inheritance:r

# 4. 使用 SID 授予完全控制权限
icacls "$FilePath" /grant:r "*$AdminSID`:F"

# 5. 验证结果
Write-Host "权限设置完成。当前权限如下：" -ForegroundColor Green
icacls "$FilePath"
```

**脚本说明**：
- 自动获取 Administrator 账户的 SID
- 使用 SID 而非用户名，避免特殊字符问题
- 授予完全控制权限（F = Full Control）
- 自动验证权限设置结果

## 完整命令模板

```powershell
# 定义变量
$serverIp = "10.10.10.1"
$workspace = "E:\Workspace"
$keyPath = "$workspace\Servers\$serverIp\id_rsa"

# 步骤 1：断开继承权限
icacls $keyPath /inheritance:r

# 步骤 2：执行权限修复脚本（替换脚本中的 {ip地址} 为实际 IP）
powershell.exe -ExecutionPolicy Bypass -File "E:\Workspace\.madorin\skills\server-maintainer\scripts\fix-key-permission.ps1"

# 步骤 3：验证连接
ssh -i $keyPath root@$serverIp -p 22
```

## 参数

| 参数名 | 类型 | 必需 | 说明 |
|--------|------|------|------|
| serverIp | String | 是 | 服务器 IP 地址，用于定位密钥文件路径 |
| workspace | String | 否 | 工作目录路径，默认为 `E:\Workspace` |
| username | String | 否 | SSH 用户名，默认为 `root` |
| port | Integer | 否 | SSH 端口，默认为 `22` |

## 执行流程

1. 接收服务器 IP 参数
2. 构建密钥文件完整路径：`{工作目录}\Servers\{IP}\id_rsa`
3. 检查密钥文件是否存在
4. **步骤 1**：执行 `icacls` 命令断开继承权限
5. **步骤 2**：执行 `fix-key-permission.ps1` 脚本设置 Administrator 完全控制权限
6. 验证权限设置是否成功
7. 可选：测试 SSH 连接
8. 返回执行结果

## 输出

- **控制台**：实时输出权限修复进度
- **返回值**：成功/失败状态，以及修复后的权限信息

## 异常处理

| 异常类型 | 处理方式 |
|----------|----------|
| 密钥文件不存在 | 报错并提示用户检查路径 |
| icacls 命令执行失败 | 检查是否有管理员权限 |
| Administrator 账户不存在 | 回退到使用 `$env:USERNAME` |
| 权限设置后仍无法连接 | 检查密钥文件格式（BOM、编码等） |

## 常见问题

### Q1: 为什么需要断开继承权限？
A: Windows 文件默认继承父目录权限，可能导致其他用户或组也有读取权限，OpenSSH 认为不安全。

### Q2: 为什么使用 SID 而不是用户名？
A: SID（安全标识符）是唯一的，不受用户名特殊字符（空格、括号等）影响，更可靠。

### Q3: 权限修复后还是无法连接？
A: 可能是密钥文件格式问题（如 BOM 标记、编码错误），需要重新下载或转换格式。

### Q4: 需要使用管理员权限吗？
A: 是的，修改文件权限需要管理员权限，请以管理员身份运行 PowerShell。

## 依赖脚本

- `fix-key-permission.ps1`: 实际执行权限修复的 PowerShell 脚本（位于 `scripts/` 目录）

## 相关文件

- 主技能：[server-maintainer](../../SKILL.md)
- 脚本：[fix-key-permission.ps1](../../scripts/fix-key-permission.ps1)

---

创建日期：2026-04-10
最后更新：2026-04-10
