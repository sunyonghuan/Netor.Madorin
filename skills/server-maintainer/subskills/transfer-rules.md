# Transfer Rules

## File Transfer Guidelines
- Use SSH plugin SFTP tools for secure file operations.
- Use `ssh_sftp_upload` for local-to-remote transfer and `ssh_sftp_download` for remote-to-local transfer.
- Poll asynchronous transfers with `ssh_sftp_transfer_poll`.
- Use `ssh_sftp_stat`, `ssh_sftp_list`, and `ssh_sftp_mkdir` for remote path checks and preparation.
- Remote-side `scp` is feasible when executed as a remote command through `ssh_session_exec`, `ssh_run_once`, or `ssh_exec_async`. Use it only for intentional remote-side/server-to-server copy workflows, not as a replacement for host-controlled SFTP transfer.
- Verify source and destination paths before transfer.
- Check available disk space on target server before large transfers.
- Log all transfer operations with timestamps.

## Supported Operations
- Upload: Local to remote via SSH plugin SFTP.
- Download: Remote to local via SSH plugin SFTP.
- Remote SCP: Remote-side/server-to-server copy via SSH plugin command/job tools.
- Sync: Directory synchronization with checksum verification

## Constraints
- Maximum single file size: 2GB
- Do not transfer system files (/etc, /var, /usr) without explicit confirmation.
- Preserve file permissions and ownership during transfer.
- Verify file integrity after transfer using checksums (md5sum/sha256sum).
- Do not invoke local `scp`, `ssh cat`, or PowerShell redirection for server file transfers.
- Do not use PowerShell/local shell to launch `scp`; if `scp` is needed, run it on the remote host via SSH plugin command/job tools and report that it is a remote command workflow.

## Error Handling
- connection timeout: Retry once, then abort.
- Permission denied: Verify remote user permissions.
- Disk full: Abort and report available space.
