# 谷歌搜索插件 V1 接口契约

## 1. 文档定位

本文档定义 Cortana.Plugins.GoogleSearch 第一版对 AI 公开的工具接口契约。
契约用于统一开发、测试、联调和后续版本兼容策略。
第一版目标聚焦网页搜索、站内搜索和图片搜索三类高频能力。

## 2. 范围与非范围

V1 覆盖初始化配置、配置查询、配置修改、网页搜索、指定站点搜索、图片搜索和搜索参数透传。
V1 不覆盖直接抓取 Google 搜索结果 HTML，也不覆盖浏览器自动化。
V1 不包含网页正文提取、页面摘要重写和用户个性化搜索历史。

## 3. Native AOT 约束

插件本体必须可 Native AOT 发布。
禁止引入运行时反射注册、动态代理生成、浏览器驱动和依赖动态代码生成的组件。
请求与响应使用强类型 DTO 与源码生成序列化上下文。

## 4. 公共请求上下文

所有工具都应支持以下公共参数。
request_timeout_ms：请求超时，默认 10000。
operation_id：调用链追踪标识，可选。

配置工具支持以下字段。
api_key：Google API Key。
search_engine_id：Programmable Search Engine 的 cx。
default_hl：默认界面语言，可选。
default_gl：默认国家地区，可选。
default_safe：默认安全搜索级别，可选。

搜索工具支持以下字段。
api_key：本次请求覆盖使用的 Google API Key，可选。
search_engine_id：本次请求覆盖使用的 cx，可选。
hl：本次请求覆盖使用的界面语言，可选。
gl：本次请求覆盖使用的国家地区，可选。

当搜索工具未显式传入 api_key 或 search_engine_id 时，应从插件数据目录中的已保存配置读取。
当数据目录配置不存在或缺失必要字段时，工具必须返回初始化提示，不得继续请求 Google API。

## 5. 统一返回结构

所有工具统一返回 JSON 对象。
基础字段为 success、code、message、data。
搜索类工具返回 data.items、data.total_results、data.search_time_seconds、data.next_start。
配置查询类工具返回 data.configured、data.search_engine_id、data.api_key_masked、data.default_hl、data.default_gl、data.default_safe。
配置写入类工具返回 data.changed、data.configured、data.config_file。

## 6. 错误码契约

固定错误码包含以下集合。
INVALID_ARGUMENT：参数缺失、越界或格式非法。
AUTH_FAILED：API Key 或 Search Engine ID 无效。
QUOTA_EXCEEDED：搜索额度耗尽。
NETWORK_ERROR：网络不可达或连接失败。
TIMEOUT：请求超时。
CONFIG_NOT_INITIALIZED：当前插件尚未完成初始化配置。
UPSTREAM_ERROR：Google API 返回未归类错误。
INTERNAL_ERROR：插件内部未分类错误。

## 7. 工具契约明细

### 7.1 google_search_get_config

用途：查询当前插件配置状态。
参数：无。
成功返回 data.configured、data.search_engine_id、data.api_key_masked、data.default_hl、data.default_gl、data.default_safe。

### 7.2 google_search_set_config

用途：初始化或更新插件配置。
必填参数：api_key、search_engine_id。
可选参数：default_hl、default_gl、default_safe。
成功返回 data.changed、data.configured、data.config_file。

### 7.3 google_search_web

用途：执行标准网页搜索。
必填参数：query。
可选参数：api_key、search_engine_id、num、start、hl、gl、safe、date_restrict、exact_terms、exclude_terms。
当 api_key 与 search_engine_id 未传入时，应回退使用已保存配置；若配置不存在则返回 CONFIG_NOT_INITIALIZED。
成功返回 data.items，项包含 title、link、display_link、snippet。

### 7.4 google_search_site

用途：执行指定站点内搜索。
必填参数：query、site。
可选参数：api_key、search_engine_id、num、start、hl、gl、safe、date_restrict。
当 api_key 与 search_engine_id 未传入时，应回退使用已保存配置；若配置不存在则返回 CONFIG_NOT_INITIALIZED。
成功返回 data.items，项包含 title、link、display_link、snippet。

### 7.5 google_search_images

用途：执行图片搜索。
必填参数：query。
可选参数：api_key、search_engine_id、num、start、hl、gl、safe、site。
当 api_key 与 search_engine_id 未传入时，应回退使用已保存配置；若配置不存在则返回 CONFIG_NOT_INITIALIZED。
成功返回 data.items，项包含 title、link、display_link、snippet、thumbnail_link、width、height、context_link。

## 8. 幂等与并发

所有工具均为只读查询，天然幂等。
服务层不需要额外串行化，但应复用 HttpClient 并统一控制超时。

## 9. 日志与可观测性

每次调用记录 time、tool_name、operation_id、success、code、latency_ms。
查询日志额外记录 normalized_query、start、num 和返回条数。
日志必须脱敏，不记录 api_key 明文。
配置写入日志只记录配置文件路径和变更字段，不记录凭据原文。

## 10. 验收标准

首次调用未初始化时，搜索工具必须返回 CONFIG_NOT_INITIALIZED，并携带清晰的补参提示。
配置查询工具必须返回脱敏后的 api_key，不能返回明文。
配置写入工具必须验证配置文件写入插件数据目录成功。
每个工具至少具备成功、非法参数、鉴权失败、额度不足四类测试。
站内搜索工具需覆盖 site 为空和 site 非法格式场景。
图片搜索工具需验证缩略图与上下文页面字段存在性。

## 11. 建议实施顺序

先实现 google_search_get_config、google_search_set_config 和 google_search_web。
再实现 google_search_site 与 google_search_images。
最后完善日志、测试、README 和 AOT 发布验证。


## 12 测试用配置
- api_key：通过环境变量 GOOGLE_SEARCH_TEST_API_KEY 提供
- search_engine_id：通过环境变量 GOOGLE_SEARCH_TEST_SEARCH_ENGINE_ID 提供