# Inventory Management Rules

## Inventory Location
- File: `Servers/Inventory.json` in workspace root.
- Format: JSON array of server objects.

## Server Object Schema
```json
{
  "id": "srv-01",
  "name": "Main Web Node",
  "ip": "10.10.10.1",
  "ssh_user": "root",
  "ssh_key_path": "Servers/Keys/10.10.10.1_id_rsa",
  "baota_panel_url": "http://10.10.10.1:8888",
  "baota_api_sk": "your_api_sk_here",
  "tags": ["web", "prod"]
}
```

## Management Rules
- Validate JSON syntax before saving.
- Ensure all required fields (ip, ssh_user, ssh_key_path) are present.
- Baota fields are optional; leave empty string if not applicable.
- Tags should be lowercase, comma-separated.
- Backup inventory before making bulk changes.

## Validation Checks
- Verify IP format (IPv4).
- Verify ssh_key_path points to existing file.
- Verify baota_panel_url format (http://ip:port).
