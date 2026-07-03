---
name: add-server
description: 规范化添加新 SSH 服务器到管理系统，包括创建文件夹结构、配置 SSH 密钥、生成服务器信息文件、验证连接等完整流程
license: MIT
user-invocable: true
---

# Add Server（添加服务器）

## 功能描述

规范化添加新 SSH 服务器到管理系统，包括创建文件夹结构、配置 SSH 密钥、生成服务器信息文件、验证连接等完整流程。支持两种场景：用户已有密钥或使用密码登录自动生成密钥。

## 调用触发条件

当用户请求：
- "添加服务器"
- "新增服务器"
- "注册服务器"
- "配置新服务器"
- 用户提供服务器 IP 并请求配置 SSH 连接

**主技能将调用此子技能执行完整的服务器添加流程。**

## 前置条件

1. 用户提供必需的服务器信息（IP、端口、用户名、登录方式）
2. 工作目录可写
3. 网络可访问目标服务器
4. 远程连接、命令执行、脚本执行和文件传输必须使用 SSH 插件工具，不要通过 PowerShell 调用 `ssh` 或 `scp`

## 必需信息收集

| 字段 | 必需 | 默认值 | 说明 |
|------|------|--------|------|
| **IP 地址** | ✅ 是 | - | 如：10.10.10.1 |
| **端口号** | ✅ 是 | 22 | SSH 端口 |
| **服务器名称/域名** | ✅ 是 | - | 如：proxy-server |
| **用户名** | ✅ 是 | root | SSH 登录用户 |
| **登录方式** | ✅ 是 | - | 密钥/密码 二选一 |
| **私钥文件路径** | 密钥登录时必需 | - | 如已有密钥，提供路径 |
| **密码** | 密码登录时必需 | - | 不存储，仅用于初次连接 |

## 执行流程

### 阶段一：信息收集与验证

1. 引导用户提供必需信息（见上表）
2. 验证 IP 地址格式是否正确
3. 验证端口号是否在有效范围（1-65535）
4. 确认登录方式（密钥/密码）

### 阶段二：创建文件夹结构

1. 创建服务器目录：`{工作目录}\Servers\{IP 地址}\`
2. 验证目录创建成功

### 阶段三：SSH 密钥配置

#### 场景 A：用户已有 SSH 私钥

1. 复制/移动私钥文件到：`{工作目录}\Servers\{IP 地址}\id_rsa`
2. 调用子技能 `fix-key-permission` 或本地 ACL 修复流程修复 Windows 密钥权限
3. 使用 SSH 插件 `ssh_connect` 测试密钥连接，并用 `ssh_session_exec` 执行 `echo SSH_KEY_AUTH_OK`
4. 调用 `ssh_disconnect` 关闭会话

#### 场景 B：用户无密钥（使用密码登录）—— SSH 插件流程

> 所有服务器端操作通过 SSH 插件执行，密钥下载通过 SSH 插件 SFTP 工具完成。不要启动本地 `ssh`/`scp` 进程，也不要通过 PowerShell 做文本转存。

**步骤 1：执行服务器端配置脚本**

使用 `ssh_run_script_once` 或 `ssh_connect` + `ssh_exec_script` 执行合并脚本，完成以下全部操作：

```bash
set -e
NEED_RESTART=0

# 检查/修复 PubkeyAuthentication
if grep -q "^PubkeyAuthentication no" /etc/ssh/sshd_config 2>/dev/null; then
    sudo sed -i 's/^PubkeyAuthentication no/PubkeyAuthentication yes/' /etc/ssh/sshd_config
    NEED_RESTART=1
fi

# 生成 ed25519 密钥对
rm -f /root/.ssh/id_rsa_new /root/.ssh/id_rsa_new.pub
ssh-keygen -t ed25519 -f /root/.ssh/id_rsa_new -N "" -q

# 激活公钥
mkdir -p /root/.ssh
cat /root/.ssh/id_rsa_new.pub >> /root/.ssh/authorized_keys

# 修复权限
chmod 700 /root/.ssh
chmod 600 /root/.ssh/authorized_keys
chown -R root:root /root/.ssh

# 重启 sshd（仅在修改了配置时）
if [ "$NEED_RESTART" = "1" ]; then
    sudo systemctl restart sshd
fi
```

**步骤 2：SFTP 下载密钥到本地**

> ⚠️ **关键步骤：必须使用 SSH 插件 SFTP 下载，保留原始二进制格式！**
> 
> SSH 私钥文件对换行符和编码极其敏感，错误的传输方式会导致 `invalid format` 错误。

1. 使用 `ssh_connect` 创建 SSH 会话。
2. 使用 `ssh_sftp_stat(sessionId, "/root/.ssh/id_rsa_new")` 确认远程私钥存在。
3. 使用 `ssh_sftp_download(sessionId, "/root/.ssh/id_rsa_new", "{工作目录}\\Servers\\{IP 地址}\\id_rsa", overwrite: true)` 下载。
4. 使用 `ssh_sftp_transfer_poll(transferId)` 等待完成。
5. 使用 `ssh_disconnect` 关闭会话。

**❌ 错误做法（禁止使用）**：

- 通过 SSH 命令读取远程私钥内容再写入本地文本文件。
- 使用 base64 文本中转远程私钥。
- 通过 PowerShell 重定向、本地 `ssh`/`scp` 或其他本地命令行工具传输服务器文件。

**验证下载结果**：
```powershell
# 检查密钥格式（应显示正确的 PEM 格式）
Get-Content "{工作目录}\Servers\{IP 地址}\id_rsa" -Head 1
# 应输出：-----BEGIN OPENSSH PRIVATE KEY-----
```

**步骤 3：修复本地密钥权限**（无需密码）

- 调用子技能 `fix-key-permission` 或执行本地 ACL 修复流程
- Windows 密钥文件权限必须：仅 Administrator 和 SYSTEM 有 FullControl

**步骤 4：验证密钥连接**（无需密码）

使用 SSH 插件 `ssh_connect`，authMethod 选择密钥认证，privateKeyPath 指向 `{工作目录}\Servers\{IP 地址}\id_rsa`。连接成功后调用 `ssh_session_exec(sessionId, "echo SSH_KEY_AUTH_OK")`，最后调用 `ssh_disconnect`。

### 阶段四：生成 server.md 文件

1. 使用模板创建 `server.md` 文件
2. 填充服务器基本信息
3. 记录创建日期和初始状态

### 阶段五：验证与测试

1. 使用密钥测试 SSH 连接
2. 获取服务器基本信息（主机名、系统版本、运行时间等）
3. 更新 `server.md` 中的系统信息
4. 确认后续连接无需密码

## 参数

| 参数名 | 类型 | 必需 | 说明 |
|--------|------|------|------|
| serverIp | String | 是 | 服务器 IP 地址 |
| port | Integer | 否 | SSH 端口，默认 22 |
| serverName | String | 是 | 服务器名称/域名 |
| username | String | 否 | SSH 用户名，默认 root |
| loginMethod | String | 是 | 登录方式：key/password |
| keyPath | String | 密钥登录时必需 | 私钥文件路径 |
| password | String | 密码登录时必需 | SSH 密码（不存储） |
| workspace | String | 否 | 工作目录路径，默认 E:\Workspace |

## 调用的工具与本地流程

| 工具/流程 | 用途 | 调用时机 |
|---------|------|---------|
| `ssh_connect` / `ssh_disconnect` | 建立和关闭 SSH 会话 | 阶段三、五 |
| `ssh_session_exec` | 验证连接、读取系统信息 | 阶段三、五 |
| `ssh_exec_script` / `ssh_run_script_once` | 执行服务器端密钥初始化脚本 | 阶段三（场景 B） |
| `ssh_sftp_stat` / `ssh_sftp_download` / `ssh_sftp_transfer_poll` | 下载远程私钥到本地 | 阶段三（场景 B） |
| 本地文件操作 | 创建服务器目录、写入 server.md、校验字段 | 阶段一、二、四 |
| 本地 ACL 修复流程 | 修复 Windows 私钥权限 | 阶段三（场景 A/B） |

## 调用的子技能

| 子技能名称 | 用途 | 调用时机 |
|-----------|------|---------|
| `fix-key-permission` | 修复密钥权限问题 | 阶段三（密钥配置后） |

## 输出文件

| 文件路径 | 说明 |
|---------|------|
| `{工作目录}\Servers\{IP 地址}\server.md` | 服务器信息文件 |
| `{工作目录}\Servers\{IP 地址}\id_rsa` | SSH 私钥文件 |

## 输出

- **控制台**：实时输出各阶段执行进度
- **文件**：创建 server.md 和 id_rsa 文件
- **返回值**：成功/失败状态，服务器目录路径

## 异常处理

| 异常场景 | 处理方式 |
|---------|---------|
| IP 地址格式错误 | 提示用户重新输入有效 IP |
| 端口号无效 | 提示有效范围（1-65535），建议使用 22 |
| 文件夹创建失败 | 检查权限，检查工作目录是否可写 |
| 服务器未开启密钥登录 | 自动执行 sed 命令修改配置并重启 sshd |
| 密钥生成失败 | 检查磁盘空间，检查 /root/.ssh 目录权限 |
| 公钥激活失败 | 手动追加公钥到 authorized_keys |
| 权限修复失败 | 逐条执行 chmod/chown 命令检查错误 |
| **密钥下载失败** | **检查网络连接，使用 SSH 插件 SFTP 下载，检查本地权限** |
| **密钥格式错误** | **使用 SSH 插件 SFTP 重新下载，不要用文本写入方式** |
| 密钥权限修复失败 | 手动执行 icacls 命令，检查用户权限 |
| SSH 连接失败 | 检查网络、防火墙、SSH 服务状态 |
| 服务器信息获取失败 | 继续流程，标记 server.md 为待更新 |

## 常见问题 FAQ

### Q1: 已有密钥文件但路径不在工作目录内怎么办？

A: 将密钥文件复制到 `{工作目录}\Servers\{IP 地址}\id_rsa`，然后执行本地 ACL 权限修复流程。

### Q2: 服务器不允许密钥登录怎么办？

A: 子技能会通过 SSH 插件 `ssh_exec_script` / `ssh_run_script_once` 检测并修改 `/etc/ssh/sshd_config` 中的 `PubkeyAuthentication` 配置，然后按需重启 sshd 服务。

### Q3: 为什么使用 ed25519 而不是 RSA？

A: ed25519 算法更安全、密钥更短、生成速度更快，是现代 SSH 的推荐算法。

### Q4: 权限修复后仍然无法连接怎么办？

A: 检查密钥文件是否损坏，尝试重新生成密钥对；检查服务器防火墙是否允许 SSH 连接。

### Q5: 能否批量添加多台服务器？

A: 当前版本支持单台添加，批量添加需逐个执行此流程。

### Q6: 下载密钥时提示权限错误怎么办？

A: 确保服务器上 `/root/.ssh/id_rsa_new` 文件权限为 600，且当前用户有读取权限。

### Q7: 如何手动执行完整的服务器密钥配置？

A: 使用 SSH 插件 `ssh_run_script_once` 或 `ssh_connect` + `ssh_exec_script` 执行服务器端配置脚本，然后用 `ssh_sftp_download` 下载生成的私钥。

### Q8: 为什么密钥下载后提示 "invalid format" 错误？

A: **这是最常见的错误！** 原因是使用了错误的下载方式：
- ❌ **错误**：通过 SSH 会话读取文本内容再写入文件（换行符从 LF → CRLF）
- ❌ **错误**：使用 base64 编码再解码（可能改变格式）
- ✅ **正确**：使用 SSH 插件 `ssh_sftp_download` 下载，保留原始二进制格式

**解决方案**：删除错误的密钥文件，重新用 `ssh_sftp_download` 下载。

### Q9: 如何验证下载的密钥格式是否正确？

A: 执行以下检查：
1. 文件大小应与服务器上一致（通常 300-500 字节）
2. 第一行应为 `-----BEGIN OPENSSH PRIVATE KEY-----`
3. 使用 SSH 插件 `ssh_connect` 测试，不应出现 `invalid format` 错误

## 用户交互示例

**用户**：我要添加一台新服务器

**助手**：好的！请提供以下信息：
1. 服务器 IP 地址
2. SSH 端口号（默认 22）
3. 服务器名称/域名
4. 用户名（默认 root）
5. 您是否有 SSH 私钥？

## 依赖工具

- SSH 插件连接工具：`ssh_connect`、`ssh_disconnect`
- SSH 插件命令工具：`ssh_session_exec`、`ssh_exec_script`、`ssh_run_script_once`
- SSH 插件 SFTP 工具：`ssh_sftp_stat`、`ssh_sftp_download`、`ssh_sftp_transfer_poll`
- 本地文件系统能力：创建目录、写入 `server.md`、修复本地私钥 ACL

## 相关文件

- 主技能：[server-maintainer](../../SKILL.md)
- 子技能：[fix-key-permission](fix-key-permission.md)

---

创建日期：2026-04-10
最后更新：2026-06-10（改为统一使用 SSH 插件工具，禁止 PowerShell/命令行 SSH 流程）
