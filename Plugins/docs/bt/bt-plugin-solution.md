# 宝塔 API 封装为 bt 插件方案

## 1. 文档目标

本文档基于 bt-api-doc.pdf 的可提取内容，给出将宝塔面板 API 封装为 Cortana 插件的落地方案。
目标是让 AI 通过稳定工具调用完成系统状态查询与网站管理。
方案重点覆盖鉴权签名、接口分组、工具契约、AOT 风险和实施路线。

## 2. Native AOT 风险评估（先行）

当前方案可按 Native AOT 友好方式实现，建议保留纯 HttpClient + System.Text.Json + 源码生成序列化。
高风险点是动态 JSON、反射式序列化、运行时代理生成和动态加载脚本。
结论是可以实施，但必须约束为强类型 DTO、静态工具入口、显式依赖注入。
如果后续要支持“任意 action 动态透传”，该能力应放到隔离模块，不进入 AOT 主插件。

## 3. API 基础信息

- 请求方式：统一 POST。
- 鉴权参数：`request_time`、`request_token`。
- 签名算法：`request_token = md5(string(request_time) + md5(api_sk))`。
- 安全要求：启用 IP 白名单。
- 会话建议：保存并复用 cookie，提高调用效率。
- 响应格式：统一 JSON。

补充说明：当前已提取页（1-13）未出现独立“上传文件”API。
因此代码交付建议走 SSH 命令模式（SCP 上传 + 远程命令部署）。

## 4. 已提取接口信息（文档页 1-13）

### 4.1 系统状态与面板

| 能力 | URI | 关键参数 | 关键返回 |
| --- | --- | --- | --- |
| 获取系统统计 | /system?action=GetSystemTotal | 无 | system, version, cpuRealUsed, memTotal |
| 获取磁盘分区 | /system?action=GetDiskInfo | 无 | path, inodes, size |
| 获取实时状态 | /system?action=GetNetWork | 无 | cpu, mem, load, down, up |
| 查询任务数 | /ajax?action=GetTaskCount | 无 | 数字任务数 |
| 检查面板更新 | /ajax?action=UpdatePanel | check, force | status, version, updateMsg |

### 4.2 网站管理

| 能力 | URI | 关键参数 | 关键返回 |
| --- | --- | --- | --- |
| 获取网站列表 | /data?action=getData&table=sites | limit(必传), p, type, order, search | data, page |
| 获取网站分类 | /site?action=get_site_types | 无 | id, name |
| 获取 PHP 版本 | /site?action=GetPHPVersion | 无 | version, name |
| 创建网站 | /site?action=AddSite | webname, path, type_id, type, version, port, ps, ftp, sql | siteStatus, ftpStatus, databaseStatus |
| 删除网站 | /site?action=DeleteSite | id, webname, ftp?, database?, path? | status, msg |
| 停用网站 | /site?action=SiteStop | id, name | status, msg |
| 启用网站 | /site?action=SiteStart | id, name | status, msg |
| 设置到期时间 | /site?action=SetEdate | id, edate | status, msg |
| 修改网站备注 | /data?action=setPs&table=sites | id, ps | status, msg |
| 获取备份列表 | /data?action=getData&table=backup | limit, type=0, search=siteId | data, page |
| 创建备份 | /site?action=ToBackup | id | status, msg |
| 删除备份 | /site?action=DelBackup | id | status, msg |
| 域名列表 | /data?action=getData&table=domain | search=siteId, list=true | id, name, port |
| 添加域名 | /site?action=AddDomain | id, webname, domain | status, msg |
| 删除域名 | /site?action=DelDomain | id, webname, domain, port | status, msg |
| 伪静态模板列表 | /site?action=GetRewriteList | siteName | rewrite[] |
| 读文件内容 | /files?action=GetFileBody | path | 文件文本 |
| 写文件内容 | /files?action=SaveFileBody | path, data, encoding=utf-8 | status, msg |
| 目录与防跨站状态 | /site?action=GetDirUserINI | id, path | pass, logs, userini, runPath |
| 防跨站开关 | /site?action=SetDirUserINI | path | status, msg |
| 日志开关 | /site?action=logsOpen | id | status, msg |
| 修改网站根目录 | /site?action=SetPath | id, path | status, msg |
| 设置运行目录 | /site?action=SetSiteRunPath | id, runPath | status, msg |
| 开启密码访问 | /site?action=SetHasPwd | id, username, password | status, msg |
| 关闭密码访问 | /site?action=CloseHasPwd | id | status, msg |
| 流量限制配置 | /site?action=GetLimitNet / SetLimitNet / CloseLimitNet | id, perserver, perip, limit_rate | status, msg |
| 默认文档 | /site?action=GetIndex / SetIndex | id, Index | status, msg |

## 5. bt 插件封装设计

建议新建插件项目：`Src/Cortana.Plugins.Bt`。
按“工具层 + 服务层 + API 客户端层”三层实现。
工具层只暴露 AI 可调用的稳定能力，不直接拼装 HTTP。
服务层负责业务编排、参数校验、幂等控制和审计日志。
API 客户端层负责签名计算、cookie 复用、重试和错误归一化。

### 5.1 建议公开工具（V1）

| 工具名 | 对应 API | 用途 |
| --- | --- | --- |
| bt_get_system_total | /system?action=GetSystemTotal | 查询主机总体状态 |
| bt_get_disk_info | /system?action=GetDiskInfo | 查询磁盘分区与容量 |
| bt_get_network_status | /system?action=GetNetWork | 查询实时 CPU/内存/网络 |
| bt_list_sites | /data?action=getData&table=sites | 分页列出网站 |
| bt_add_site | /site?action=AddSite | 创建网站（可选附带 FTP/数据库） |
| bt_delete_site | /site?action=DeleteSite | 删除网站及关联资源 |
| bt_set_site_status | /site?action=SiteStart 或 SiteStop | 启停网站 |
| bt_set_site_expire | /site?action=SetEdate | 设置到期时间 |
| bt_manage_site_domain | /site?action=AddDomain 或 DelDomain | 增删网站域名 |
| bt_site_backup | /site?action=ToBackup 或 DelBackup | 创建或删除备份 |
| bt_get_or_set_site_config | /files?action=GetFileBody 或 SaveFileBody | 读写站点配置文件 |
| bt_set_site_security | /site?action=SetDirUserINI, SetHasPwd, CloseHasPwd | 防跨站与密码访问 |
| bt_set_site_limit | /site?action=SetLimitNet 或 CloseLimitNet | 流量与并发限制 |
| bt_upload_site_code | SSH/SCP | 上传站点代码到网站根目录 |
| bt_deploy_site_package | SSH/SCP + 远程解压命令 | 上传压缩包并解压部署到运行目录 |

## 6. 公共契约建议

- 全工具公共参数：`panel_url`、`api_key`、`api_sk`、`request_timeout_ms`、`operation_id`。
- 工具返回统一结构：`success`、`code`、`message`、`data`。
- 失败码建议：`INVALID_ARGUMENT`、`AUTH_FAILED`、`BT_API_ERROR`、`NETWORK_ERROR`、`TIMEOUT`、`INTERNAL_ERROR`。
- 所有写操作返回 `changed` 与关键对象标识（如 `site_id`、`domain`、`backup_id`）。

## 7. 分阶段实施建议

第一阶段：完成签名客户端、系统状态查询和网站列表读取，打通最小闭环。
第二阶段：完成网站创建/删除、域名增删、启停与到期设置。
第三阶段：完成代码上传（SSH/SCP）、备份、配置文件读写、防跨站和密码访问能力。
第四阶段：补充压缩包部署、限流、默认文档和更细粒度站点运维能力。

## 9. 代码上传方案（新增）

### 9.1 推荐路径（V1）

1. 使用服务器 IP、SSH 用户和密钥建立 SSH 连接。
2. 通过 SCP 将构建产物上传到站点目录（或临时发布目录）。
3. 上传完成后返回文件数量、总字节数和目标目录。

### 9.2 可选路径（V2）

1. 上传 zip 或 tar.gz 包到站点目录。
2. 远程执行解压命令并按覆盖策略发布。
3. 可选执行发布后步骤（如目录切换、权限修正、缓存清理）。

可用解压命令示例：
- zip：`unzip -o package.zip -d /www/wwwroot/site`
- tar.gz：`tar -xzf package.tar.gz -C /www/wwwroot/site`

结论：命令模式下可以解压，且适合做可审计的自动化发布。

### 9.3 安全要求

- 仅允许上传到站点根目录白名单。
- 默认禁止覆盖敏感文件（如 `.user.ini`、`vhost` 配置）。
- 记录上传摘要日志，避免记录文件正文与密钥明文。

## 8. 验收与发布要求

- 每个工具至少覆盖成功、参数非法、鉴权失败三类测试。
- 写操作需在测试环境执行并校验资源实际变化。
- 发布前执行 Native AOT 发布验证，禁止新增 trimming/AOT 阻断告警。
- 建议保留接口回放日志（脱敏），用于失败复盘。
- 本方案接口信息来源于 bt-api-doc.pdf 转文本结果 `bt-api-doc.txt`（页 1-13）。










