# Transfer Policy

## 规则

- 上传前确认本地源路径和远端目标路径。
- 下载前确认远端源路径和本地目标路径。
- 默认不覆盖同名文件。
- 覆盖、批量传输、跨目录同步需要额外确认。
- 主机可控的上传/下载优先使用 SSH 插件 SFTP 工具：`ssh_sftp_upload`、`ssh_sftp_download`、`ssh_sftp_transfer_poll`。
- 远端 `scp` 命令可以通过 `ssh_session_exec`、`ssh_run_once` 或 `ssh_exec_async` 执行，适用于远端发起的服务器到服务器复制；不要通过 PowerShell 或本地 shell 启动 `scp`。

## 返回结果

- 返回源路径、目标路径、是否覆盖、是否成功。
