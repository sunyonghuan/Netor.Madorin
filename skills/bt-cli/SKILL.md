---
name: bt-cli
description: Use the bundled Native AOT Baota panel CLI for single-server operations, server-matrix orchestration, website management, system queries, and config edits. Trigger when the user asks to operate BaoTa/宝塔 panels through a CLI, run batch operations across multiple servers, inspect sites/disks/network/PHP versions, or manage site domains, backups, status, config files, security settings, traffic limits, paths, and notes.
---

# BT CLI

## Overview

Use the bundled `bt-cli` executable to operate one Baota panel target per process invocation. The CLI is intentionally stateless: never create or rely on local server config files, saved profiles, or persisted API keys.

## Executable

Use the Windows asset when running in this repository:

```powershell
skills\bt-cli\assets\win-x64\bt-cli.exe
```

If the asset is missing, build and publish `Plugins/Src/Cortana.Plugins.Bt.Cli/Cortana.Plugins.Bt.Cli.csproj` with Native AOT, then copy the published executable into `assets/win-x64`.

## Target Rules

Pass the target server explicitly on every call:

```powershell
bt-cli.exe list-sites --panel-url http://127.0.0.1:8888 --api-sk <key> --limit 20 --page 1
```

You may use temporary process environment variables only when they are scoped to the current shell:

```powershell
$env:BT_PANEL_URL = "http://127.0.0.1:8888"
$env:BT_API_SK = "<key>"
bt-cli.exe system-total
```

Do not write server URLs, API keys, or server matrices to skill assets, config files, appsettings files, user secrets, or profile stores.

## Matrix Workflow

For multiple servers, let the agent orchestrate the matrix:

1. Resolve the target server list from the user-provided context or another approved source.
2. Call `bt-cli` once per server with that server's `panelUrl` and `apiSk`.
3. Capture each JSON result.
4. Aggregate success/failure by server name outside the CLI.

The CLI should remain a single-server executor. Do not invent `config add`, `config use`, or saved profile workflows.

## Output Contract

The CLI prints one JSON object to stdout:

```json
{
  "success": true,
  "code": "OK",
  "message": "Sites fetched.",
  "response": {
    "success": true,
    "statusCode": 200,
    "requestUrl": "http://panel:8888/data?action=getData&table=sites",
    "responseJson": "{...}"
  }
}
```

Treat `success=false` as failure. Inspect `code`, `message`, `errors`, and `response.responseJson` before retrying or escalating.

## High-Risk Operations

Only add `--yes` after the user explicitly confirmed the exact high-risk action and target server/site:

- deleting a site or backup
- stopping a site
- overwriting a config file
- changing security settings
- changing traffic limits
- changing site root or run paths
- using the generic `call` command

If confirmation is missing, ask for it instead of running with `--yes`.

## Common Commands

System and inventory:

```powershell
bt-cli.exe system-total --panel-url <url> --api-sk <key>
bt-cli.exe disk-info --panel-url <url> --api-sk <key>
bt-cli.exe network-status --panel-url <url> --api-sk <key>
bt-cli.exe list-sites --panel-url <url> --api-sk <key> --limit 20 --page 1
bt-cli.exe php-versions --panel-url <url> --api-sk <key>
```

Site resources:

```powershell
bt-cli.exe site-domains --panel-url <url> --api-sk <key> --site-id <id>
bt-cli.exe site-backups --panel-url <url> --api-sk <key> --site-id <id> --limit 10 --page 1
bt-cli.exe site-security-state --panel-url <url> --api-sk <key> --id <id> --path <root>
```

Site management:

```powershell
bt-cli.exe add-site --panel-url <url> --api-sk <key> --domain demo.example.com --path /www/wwwroot/demo --version 80 --ps "demo"
bt-cli.exe site-status --panel-url <url> --api-sk <key> --id <id> --name demo.example.com --target stop --yes
bt-cli.exe site-domain --panel-url <url> --api-sk <key> --action add --id <id> --webname demo.example.com --domain www.demo.example.com
```

Config and settings:

```powershell
bt-cli.exe site-config get --panel-url <url> --api-sk <key> --path /www/server/panel/vhost/nginx/demo.conf
bt-cli.exe site-config set --panel-url <url> --api-sk <key> --path /www/server/panel/vhost/nginx/demo.conf --data-file .\demo.conf --yes
bt-cli.exe site-index get --panel-url <url> --api-sk <key> --id <id>
bt-cli.exe site-limit --panel-url <url> --api-sk <key> --action get --id <id>
```

Generic fallback:

```powershell
bt-cli.exe call --panel-url <url> --api-sk <key> --path "/system?action=GetSystemTotal" --yes
bt-cli.exe call --panel-url <url> --api-sk <key> --path "/data?action=getData&table=sites" --field limit=20 --field p=1 --field type=0 --yes
```

Large field content:

Use request-content files for long payload values such as certificates, private keys, large JSON values, or multi-line config bodies. These are single-call payload inputs, not saved server configuration.

```powershell
bt-cli.exe call --panel-url <url> --api-sk <key> --path "/site?action=SetSSL" --field siteName=demo.example.com --field-file cert=.\cert.pem --field-file key=.\privkey.pem --yes
```

Use stdin only when one long field comes from an upstream command:

```powershell
Get-Content .\cert.pem -Raw | bt-cli.exe call --panel-url <url> --api-sk <key> --path "/site?action=SetSSL" --field siteName=demo.example.com --field-stdin cert --yes
```

Prefer `--field-file key=path` when multiple long fields are needed.
