# Cortana.Plugins.Bt

## 1. 项目说明

这是一个面向宝塔面板的 Cortana 插件。

插件通过宝塔 HTTP API 提供系统查询、站点管理和站点配置能力。
当前版本不负责文件上传与部署工作流，这部分预留给外部技能或独立工作流完成。

## 2. 设计原则

1. 工具层只暴露稳定能力，不直接拼装复杂业务流程。
2. API 客户端层只负责签名、请求发送和响应返回。
3. 工具按职责拆分为多个小文件，避免大文件堆叠。
4. 所有序列化均使用 Source Generator，保持 Native AOT 兼容。

## 3. 当前工具清单

### 3.1 系统查询

- bt_get_system_total：查询系统总体状态。
- bt_get_disk_info：查询磁盘分区信息。
- bt_get_network_status：查询 CPU、内存、网络与负载状态。

### 3.2 站点查询

- bt_list_sites：分页查询网站列表。
- bt_get_php_versions：查询已安装 PHP 版本列表。
- bt_get_site_domains：查询指定网站的域名列表。
- bt_get_site_backups：查询指定网站的备份列表。
- bt_get_site_security_state：查询防跨站、日志、密码访问与运行目录状态。

### 3.3 站点管理

- bt_add_site：创建网站。
- bt_delete_site：删除网站。
- bt_set_site_status：启用或停用网站。
- bt_manage_site_domain：添加或删除网站域名。
- bt_site_backup：创建或删除网站备份。
- bt_set_site_note：修改网站备注。
- bt_set_site_expire：设置网站到期时间。

### 3.4 站点配置

- bt_get_or_set_site_config：读取或保存网站配置文件内容。
- bt_get_or_set_site_index：读取或设置默认文档。
- bt_set_site_security：设置防跨站、密码访问或日志开关。
- bt_set_site_limit：获取、设置或关闭流量限制。
- bt_set_site_path：修改网站根目录。
- bt_set_site_run_path：设置网站运行目录。

## 4. 推荐使用方式

1. 先调用查询类工具获取当前状态，再执行修改类工具。
2. 高风险操作（删除、停用、覆盖配置）应在用户明确确认后执行。
3. 文件上传、压缩包部署、远程命令执行建议放到外部工作流，不在本插件内部完成。

## 5. 当前不负责的能力

1. 代码上传。
2. SSH 部署。
3. 压缩包解压发布。
4. 任意文件系统透传。

## 6. 代码结构

- Startup.cs：插件入口与 DI 注册。
- BtApiClient.cs：宝塔 API 调用客户端。
- BtRequestSigner.cs：签名生成。
- ToolResult.cs：统一返回结构。
- BtSystemTools.cs：系统查询工具。
- BtSite*.cs：按站点能力分组的工具文件。
