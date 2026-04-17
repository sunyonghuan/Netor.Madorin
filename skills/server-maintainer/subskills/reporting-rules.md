# Reporting Rules

## Report Generation Guidelines
- Aggregate results from all servers into a single Markdown table.
- Include timestamp, server name, IP, status, and key metrics.
- Highlight errors and warnings in a separate summary section.
- Save reports to `Reports/` directory with date-based naming.

## Report Structure
```markdown
# Server Status Report - YYYY-MM-DD HH:MM

## Summary
- Total Servers: X
- Online: X
- Offline: X
- Warnings: X

## Details
| Server | IP | Status | CPU | Memory | Disk | Load |
|--------|----|--------|-----|--------|------|------|
| Web-01 | 10.10.10.1 | ✅ | 45% | 60% | 75% | 1.2 |
| DB-01  | 10.10.10.2 | ❌ | - | - | - | - |

## Errors
- DB-01: Connection timeout (SSH key invalid)

## Next Steps
- Investigate DB-01 connectivity
- Schedule maintenance for Web-01 disk cleanup
```

## Best Practices
- Use consistent formatting across all reports.
- Include actionable recommendations.
- Archive old reports monthly.
- Do not include sensitive data (passwords, API keys) in reports.
