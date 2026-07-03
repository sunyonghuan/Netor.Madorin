---
name: file-transfer-subskill
description: 文件上传下载
version: 1.2.1
---

# 文件传输子技能

## 功能描述
在本地和服务器之间上传/下载文件。

## 使用场景
- 部署应用文件
- 备份服务器数据
- 日志文件下载分析
- 配置文件更新
- **SSH 密钥文件下载（关键场景）**

## 传输方式
- **SSH 插件 SFTP 工具（推荐，保留原始格式）**
- `ssh_sftp_upload`：上传本地文件到远程路径
- `ssh_sftp_download`：下载远程文件到本地路径
- `ssh_sftp_transfer_poll`：轮询异步传输状态
- `ssh_sftp_stat` / `ssh_sftp_list`：检查远程路径和目录
- **远端 SCP 命令（可行但不是主传输工具）**
- 可通过 `ssh_session_exec`、`ssh_run_once` 或 `ssh_exec_async` 在远端执行 `scp ...`，适合远端发起的服务器到服务器复制。
- 不要通过 PowerShell 或本地 shell 启动 `scp`；主机到服务器、服务器到主机的传输优先使用 SSH 插件 SFTP 工具。

## 支持操作
- 单文件上传/下载
- 批量文件传输
- 文件夹同步
- 断点续传

---

## 🔑 SSH 密钥文件下载规范（关键）

### ⚠️ 重要警告

**SSH 私钥文件对格式极其敏感**，错误的下载方式会导致 `invalid format` 错误，无法用于认证。

### ❌ 错误做法（禁止使用）

- 错误 1：通过 SSH 命令读取远程私钥文本，再用本地文本写入文件。
- 错误 2：通过 base64 文本中转远程私钥，再在本地解码。
- 错误 3：通过 PowerShell 重定向保存远程私钥内容。

**为什么错误？**
- Windows 文本写入会自动转换换行符（`\n` → `\r\n`）
- OpenSSH 私钥对换行符和编码极其敏感
- CRLF 换行符会导致 `Load key "xxx": invalid format` 错误

### ✅ 正确做法（必须使用）

#### 方法 A：使用 SSH 插件 SFTP 下载（推荐）

1. 使用 `ssh_connect` 创建 SSH 会话。
2. 使用 `ssh_sftp_stat` 确认远程私钥文件存在。
3. 使用 `ssh_sftp_download(sessionId, remotePath, localPath, overwrite)` 直接下载到本地。
4. 使用 `ssh_sftp_transfer_poll` 等待下载完成。
5. 使用 `ssh_disconnect` 关闭 SSH 会话。

**优点：**
- ✅ 二进制传输，保留原始格式
- ✅ 换行符不变（保持 Linux LF 格式）
- ✅ 无编码转换风险
- ✅ 不依赖本地 `scp` 命令或 PowerShell 重定向
- ✅ 避免把远端 `scp` 命令场景与本地主机下载私钥场景混淆

---

### 📋 密钥下载完整流程

#### 步骤 1：确认服务器端密钥位置
1. 使用 `ssh_connect` 连接目标服务器。
2. 使用 `ssh_sftp_list(sessionId, "/root/.ssh")` 查看远程目录。
3. 使用 `ssh_sftp_stat(sessionId, "/root/.ssh/id_rsa_new")` 确认密钥文件存在且大小合理。

#### 步骤 2：使用 SFTP 下载密钥
1. 调用 `ssh_sftp_download(sessionId, "/root/.ssh/id_rsa_new", "{工作目录}\\Servers\\10.10.10.5\\id_rsa", overwrite: true)`。
2. 调用 `ssh_sftp_transfer_poll(transferId)` 直到状态为完成或失败。

#### 步骤 3：验证下载结果
```powershell
# 检查文件大小
Get-Item "E:\Workspace\Servers\10.10.10.5\id_rsa" | Select-Object Length

# 检查文件格式（应显示 OPENSSH PRIVATE KEY）
Get-Content "E:\Workspace\Servers\10.10.10.5\id_rsa" -Head 1

# 检查换行符（应为纯 LF）
$bytes = [System.IO.File]::ReadAllBytes("E:\Workspace\Servers\10.10.10.5\id_rsa")
$hasCRLF = $bytes -contains 13  # 13 = CR (\r)
if ($hasCRLF) { Write-Host "警告：发现 CRLF 换行符！" } else { Write-Host "✓ 换行符正确（纯 LF）" }
```

#### 步骤 4：修复本地权限（Windows 必需）
```powershell
# 断开继承
$acl = Get-Acl "E:\Workspace\Servers\10.10.10.5\id_rsa"
$acl.SetAccessRuleProtection($true, $false)

# 添加 Administrator 和 SYSTEM 完全控制
$rule1 = New-Object System.Security.AccessControl.FileSystemAccessRule("Administrator", "FullControl", "Allow")
$rule2 = New-Object System.Security.AccessControl.FileSystemAccessRule("SYSTEM", "FullControl", "Allow")
$acl.AddAccessRule($rule1)
$acl.AddAccessRule($rule2)

# 应用权限
Set-Acl "E:\Workspace\Servers\10.10.10.5\id_rsa" $acl
```

#### 步骤 5：测试密钥连接
使用 SSH 插件 `ssh_connect`，authMethod 使用密钥认证，privateKeyPath 指向下载后的本地密钥。连接成功后可用 `ssh_session_exec(sessionId, "echo SSH_KEY_AUTH_OK")` 验证命令执行，再调用 `ssh_disconnect`。

---

### 🚨 常见错误与解决方案

| 错误信息 | 原因 | 解决方案 |
|----------|------|----------|
| `Load key "xxx": invalid format` | 换行符错误（CRLF）或格式损坏 | **重新用 SFTP 下载**，不要文本写入 |
| `Permission denied (publickey)` | 密钥权限错误或服务器端 authorized_keys 不匹配 | 检查本地权限 + 验证服务器端公钥 |
| `connection timed out` | 网络问题或服务器无响应 | 检查网络连接 + 服务器状态 |
| `WARNING: UNPROTECTED PRIVATE KEY FILE!` | 权限过于开放（Linux） | `chmod 600 id_rsa` |

---

## 安全注意
- 验证文件完整性（MD5/SHA256）
- 传输加密
- 权限设置（密钥文件必须限制访问）
- 敏感文件保护
- **密钥文件永远不要用文本方式传输或编辑**
- **不要通过 PowerShell 或本地 shell 调用 `scp`、`ssh cat` 或重定向下载服务器文件**
- **如果确实需要 `scp`，只能把它作为远端命令通过 SSH 插件命令/任务工具执行，并明确这是远端发起的复制流程**

---

## 版本历史
- **v1.2.1** (2026-06-10): 明确远端 `scp` 可通过 SSH 插件命令/任务工具执行，但本地 PowerShell/命令行 `scp` 仍禁止
- **v1.2.0** (2026-06-10): 改为统一使用 SSH 插件 SFTP 工具，禁止通过 PowerShell/命令行 `scp` 或 `ssh cat` 传输
- **v1.1.0** (2026-04-10): 添加 SSH 密钥下载规范
- v1.0.0: 初始版本
