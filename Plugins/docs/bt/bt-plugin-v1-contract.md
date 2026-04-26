# bt 插件 V1 接口契约

## 1. 文档定位

本文档定义 Cortana.Plugins.Bt 第一版对 AI 公开的工具接口契约。
契约用于统一开发、测试、联调和后续版本兼容策略。
第一版目标聚焦系统状态查询与网站管理高频能力。

## 2. 范围与非范围

V1 覆盖系统状态、站点列表、站点创建删除、启停、域名管理、备份、基础安全设置和代码上传部署。
V1 不覆盖面板所有 action 的动态透传，也不开放任意文件系统写入。
V1 不包含面板安装更新等高风险运维动作。

## 3. Native AOT 约束

插件本体必须可 Native AOT 发布。
禁止引入运行时反射注册、动态代理生成和依赖动态代码生成的组件。
请求与响应使用强类型 DTO 与源码生成序列化上下文。

## 4. 版本与命名约定

插件标识建议为 bt。
工具名前缀统一为 bt_。
本文档契约版本为 1.0.0。
新增可选字段不视为破坏性变更。
删除字段或改变字段语义视为破坏性变更，必须升级主版本。

## 5. 公共请求上下文

所有工具都应支持以下公共参数。
panel_url：宝塔面板地址。
api_sk：接口密钥。
request_timeout_ms：请求超时，默认 10000。
operation_id：调用链追踪标识，可选。
ssh_host：服务器 IP 或域名（用于命令式上传/部署）。
ssh_port：SSH 端口，默认 22。
ssh_user：SSH 用户名。
ssh_key：SSH 私钥内容或密钥引用。

所有请求采用 POST。
每次请求都必须携带 request_time 与 request_token。
签名计算规则为 request_token = md5(string(request_time) + md5(api_sk))。

## 6. 统一返回结构

所有工具统一返回 JSON 对象。
基础字段为 success、code、message、data。
写操作必须返回 changed 布尔值与关键对象标识。
批量写操作必须返回 changed_count。

## 7. 错误码契约

固定错误码包含以下集合。
INVALID_ARGUMENT：参数缺失、越界或格式非法。
AUTH_FAILED：鉴权失败或签名无效。
NETWORK_ERROR：网络不可达或连接失败。
TIMEOUT：请求超时。
BT_API_ERROR：宝塔接口返回失败状态。
NOT_FOUND：目标站点、域名或备份不存在。
PATH_FORBIDDEN：路径不在白名单目录或命中敏感目录。
INTERNAL_ERROR：插件内部未分类错误。
SSH_CONNECT_FAILED：SSH 连接失败。
DEPLOY_COMMAND_FAILED：远程部署命令执行失败。
DECOMPRESS_FAILED：解压失败。

错误码语义一旦发布不得重定义。

## 8. 工具契约明细

### 8.1 bt_get_system_total

用途：查询主机基础状态。
参数：仅公共参数。
成功返回 data.system、data.version、data.cpuRealUsed、data.memTotal、data.memRealUsed。

### 8.2 bt_get_disk_info

用途：查询分区容量与 inode 使用。
参数：仅公共参数。
成功返回 data.disks 列表，每项包含 path、inodes、size。

### 8.3 bt_get_network_status

用途：查询实时 CPU、内存、网络与负载。
参数：仅公共参数。
成功返回 data.cpu、data.mem、data.load、data.down、data.up。

### 8.4 bt_list_sites

用途：分页查询站点列表。
必填参数：limit。
可选参数：p、type、order、search。
成功返回 data.items、data.total_hint、data.page_html。

### 8.5 bt_add_site

用途：创建站点，可选一并创建 FTP 与数据库。
必填参数：webname、path、type_id、type、version、port、ps、ftp、sql。
可选参数：ftp_username、ftp_password、codeing、datauser、datapassword。
成功返回 data.siteStatus、data.ftpStatus、data.databaseStatus 及对应账号信息。

### 8.6 bt_delete_site

用途：删除站点及可选关联资源。
必填参数：id、webname。
可选参数：ftp、database、path。
成功返回 data.status、data.msg、data.changed。

### 8.7 bt_set_site_status

用途：启用或停用站点。
必填参数：id、name、target_status。
target_status 取值 start 或 stop。
成功返回 data.status、data.msg、data.new_status。

### 8.8 bt_set_site_expire

用途：设置站点到期时间。
必填参数：id、edate。
edate 支持 0000-00-00 表示永久。
成功返回 data.status、data.msg、data.edate。

### 8.9 bt_manage_site_domain

用途：统一管理域名增删。
必填参数：id、webname、action。
当 action=add 时必填 domain。
当 action=delete 时必填 domain、port。
成功返回 data.status、data.msg、data.action。

### 8.10 bt_site_backup

用途：创建或删除站点备份。
必填参数：action。
当 action=create 时必填 id。
当 action=delete 时必填 backup_id。
成功返回 data.status、data.msg、data.action。

### 8.11 bt_get_or_set_site_config

用途：读取或写入站点配置文件内容。
必填参数：action、path。
当 action=set 时必填 data。
写入时 encoding 固定 utf-8。
成功返回 data.file_body 或 data.status。

### 8.12 bt_set_site_security

用途：设置防跨站与密码访问。
必填参数：id、action。
action 取值 toggle_userini、set_pwd、close_pwd。
当 action=set_pwd 时必填 username、password。
成功返回 data.status、data.msg、data.action。

### 8.13 bt_set_site_limit

用途：设置或关闭站点流量限制。
必填参数：id、action。
action 取值 set 或 close。
当 action=set 时必填 perserver、perip、limit_rate。
成功返回 data.status、data.msg、data.action。

### 8.14 bt_upload_site_code

用途：通过 SSH/SCP 上传站点代码。
必填参数：ssh_host、ssh_user、ssh_key、local_path、remote_path。
可选参数：ssh_port、exclude_patterns、overwrite。
成功返回 data.uploaded_files、data.uploaded_bytes、data.remote_path。

### 8.15 bt_deploy_site_package

用途：上传压缩包并远程解压部署。
必填参数：ssh_host、ssh_user、ssh_key、local_package_path、remote_package_path、deploy_path、archive_type。
archive_type 取值 zip 或 tar.gz。
可选参数：ssh_port、clean_before_deploy、post_commands。
成功返回 data.deployed_path、data.archive_type、data.changed、data.command_output。

## 9. 幂等与并发

读取类工具应天然幂等。
写操作不保证天然幂等，建议调用方使用 operation_id 防重。
同一站点的并发写操作必须在服务层串行化。

## 10. 安全策略

必须启用面板 API 白名单。
配置写入相关工具必须校验 path 白名单。
禁止写入系统敏感路径和非站点配置目录。
日志必须脱敏，不记录 api_sk 明文。
SSH 密钥仅允许内存使用，不落盘，不写入日志。

## 11. 日志与可观测性

每次调用记录 time、tool_name、operation_id、success、code、latency_ms。
写操作额外记录 changed、site_id 与关键对象标识。
错误日志记录异常类型和简化堆栈。

## 12. 验收标准

每个工具至少具备成功、非法参数、鉴权失败三类测试。
写操作类工具增加不存在目标与并发冲突测试。
联调时必须在测试面板验证实际资源变化。
发布前必须执行 Native AOT 发布验证且无新增阻断告警。

## 13. 建议实施顺序

先实现 bt_get_system_total、bt_get_disk_info、bt_list_sites。
再实现 bt_add_site、bt_delete_site、bt_set_site_status、bt_manage_site_domain。
最后实现 bt_upload_site_code、bt_deploy_site_package、bt_site_backup、bt_get_or_set_site_config、bt_set_site_security、bt_set_site_limit。
