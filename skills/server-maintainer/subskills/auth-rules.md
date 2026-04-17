# Authentication & Security Rules

## SSH Key Management
- All SSH keys stored in `Servers/Keys/` directory.
- Naming convention: `{IP}_id_rsa` (e.g., `10.10.10.1_id_rsa`).
- Keys are plaintext for private environment; no decryption needed.
- Validate key file exists before attempting connection.

## Baota API Credentials
- Panel URL and API SK stored in `Servers/Inventory.json`.
- Never expose credentials in logs or error messages.
- Validate API connectivity with a simple request before bulk operations.

## Security Guidelines
- Do not modify system firewall rules unless explicitly requested.
- Do not change root passwords or SSH configurations.
- Log all authentication attempts for audit purposes.
- If key is invalid or corrupted, report error and skip server.
