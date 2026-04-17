# Transfer Rules

## File Transfer Guidelines
- Use SSH-based transfer for secure file operations.
- Verify source and destination paths before transfer.
- Check available disk space on target server before large transfers.
- Log all transfer operations with timestamps.

## Supported Operations
- Upload: Local to Remote via SFTP/SCP
- Download: Remote to Local via SFTP/SCP
- Sync: Directory synchronization with checksum verification

## Constraints
- Maximum single file size: 2GB
- Do not transfer system files (/etc, /var, /usr) without explicit confirmation.
- Preserve file permissions and ownership during transfer.
- Verify file integrity after transfer using checksums (md5sum/sha256sum).

## Error Handling
- Connection timeout: Retry once, then abort.
- Permission denied: Verify remote user permissions.
- Disk full: Abort and report available space.
