---
name: reporting
description: '服务器监控与报表子技能。用于采集状态、应用阈值、生成 Markdown 报表并保存到 Reports。'
user-invocable: true
---

# Server Reporting

## Scope

- 批量巡检服务器状态。
- 应用 CPU、内存、硬盘、连接阈值。
- 生成 Markdown 报表并保存到 Reports。

## Rules

- 远程指标采集必须使用 SSH 插件工具，优先使用 `ssh_connect` + `ssh_session_exec`，一次性采集可使用 `ssh_run_once`。
- 不要通过 PowerShell 启动 `ssh` 或运行旧报表脚本来采集远程指标。
- 报表文件名固定为 YYYYMMDD.md。
- 当天重复生成时覆盖当日文件。
- 连接失败的服务器必须继续出现在报表中，状态标记为离线或失败。
- 阈值判断统一读取资源配置，不在多个脚本里重复硬编码。

## Legacy Scripts

- `scripts/generate-monitor-report.ps1` 是旧模板。生成报表时直接用 SSH 插件采集数据，然后在本地写入 Markdown 报表。

## Resources

- resources/report-template.md
- resources/alert-rules.md
