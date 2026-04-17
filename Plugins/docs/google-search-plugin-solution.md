# 谷歌搜索插件项目方案

## 1. 文档目标

本文档给出一个适配当前 Cortana Native 插件体系的谷歌搜索插件方案。
目标是让 AI 通过稳定工具调用完成 Google 搜索结果获取，而不是直接抓取 Google 页面。
方案重点覆盖官方 API 选型、工具边界、AOT 风险和分阶段实施路线。

## 2. Native AOT 风险评估（先行）

当前方案可按 Native AOT 友好方式实现，建议保留纯 HttpClient + System.Text.Json + 源码生成序列化。
高风险路线是直接抓取 google.com HTML、引入浏览器自动化、依赖非官方抓取库或动态 JSON 路由。
这些路线既容易受页面结构变化影响，也会引入更高的合规与维护风险。
结论是可以实施，但必须限定为 Google Custom Search JSON API（Programmable Search Engine）这类官方 HTTP JSON 接口。

## 3. 目标与非目标

目标范围包括网页搜索、站内搜索、图片搜索和结构化搜索结果返回。
目标范围包括安全搜索、语言区域参数和分页参数的受控透传。
非目标包括网页正文抓取、登录态搜索、广告点击、搜索建议和任意 Google 产品接口透传。
第一版默认只对接 Custom Search JSON API，不接入非官方 SERP 抓取服务。

## 4. 官方 API 路线

建议采用 Google Custom Search JSON API，使用 key 与 cx 发起查询。
网页搜索走默认搜索模式，图片搜索通过 searchType=image 开启。
站内搜索通过 siteSearch 与 siteSearchFilter=i 做显式约束，而不是让 AI 自己拼 site: 语法。
这样可以降低提示词歧义，也方便服务层做参数校验和错误归一化。

## 5. 总体架构

建议采用三层结构。
第一层是 Tool Layer，负责对 AI 暴露稳定工具、校验参数和返回统一结果。
第二层是 GoogleSearchService，负责查询构造、参数裁剪、响应归一化和错误映射。
第三层是 GoogleSearchClient，负责 HTTP 请求发送、超时控制和上游错误解析。

## 6. 初始化与配置管理

插件首次使用时，应优先检查当前插件数据目录中的配置文件是否存在且内容完整。
配置项至少包括 Google API Key 与 Programmable Search Engine ID，也就是 key 与 cx。
如果用户首次调用搜索工具时尚未完成初始化，工具不得继续调用上游 API，而应明确提示用户先补全相应参数。
建议提示文案直接说明缺失项，例如“当前谷歌搜索插件尚未初始化，请先提供 API Key 和 Search Engine ID”。

配置文件应直接存放在插件传入的数据目录中，不依赖外部注册表、环境变量或用户目录。
这样可以保持插件自包含，也便于后续迁移、备份和按插件隔离配置。

同时应提供配置查询与修改能力。
用户可以通过专用工具查看当前配置状态，也可以更新已保存的 key、cx 或默认搜索参数。
出于安全考虑，配置查询工具返回的 api_key 应做脱敏处理，不直接回显明文。

## 7. 建议公开工具（V1）

| 工具名 | 对应模式 | 用途 |
| --- | --- | --- |
| google_search_get_config | 配置读取 | 查询当前初始化状态与已保存配置摘要 |
| google_search_set_config | 配置写入 | 初始化或更新 API Key、Search Engine ID 与默认参数 |
| google_search_web | 默认搜索 | 查询网页结果 |
| google_search_site | 默认搜索 + siteSearch | 查询指定站点内结果 |
| google_search_images | searchType=image | 查询图片结果 |

搜索工具执行时优先使用数据目录中的已保存配置。
调用方显式传入 api_key 或 search_engine_id 时，可作为本次请求覆盖值，但不自动改写已保存配置。
当请求参数与本地配置都缺失时，搜索工具必须返回初始化提示，而不是继续访问上游接口。

所有工具统一返回标题、链接、摘要、来源域名和查询上下文。
图片搜索额外返回缩略图、原图尺寸和上下文页面。
第一版不开放任意参数直通，只保留经过白名单校验的常用搜索参数。

## 8. 公共契约建议

- 配置工具参数：api_key、search_engine_id、default_hl、default_gl、default_safe。
- 搜索工具公共参数：query、request_timeout_ms、operation_id。
- 搜索工具可选覆盖参数：api_key、search_engine_id、hl、gl、safe、date_restrict。
- 可选搜索参数：num、start、hl、gl、safe、date_restrict。
- 统一返回结构：success、code、message、data。
- 配置修改属于受控写操作，目标仅限插件数据目录中的配置文件。

## 9. 分阶段实施建议

第一阶段：完成配置文件读写、初始化校验、google_search_get_config、google_search_set_config 和 google_search_web，打通最小闭环。
第二阶段：完成 google_search_site 与 google_search_images，并补充分页和安全搜索参数。
第三阶段：补充测试、README、AOT 发布验证和错误码归一化。

## 10. 验收与发布要求

- 首次调用未初始化时，必须返回明确提示，引导用户先配置 API Key 与 Search Engine ID。
- 配置查询工具必须验证脱敏输出，配置写入工具必须验证数据目录实际落盘。
- 每个工具至少覆盖成功、非法参数、鉴权失败、额度不足四类测试。
- 图片搜索需额外校验缩略图与原图链接字段的稳定返回。
- 发布前执行 Native AOT 发布验证，禁止新增 trimming/AOT 阻断告警。
- 文档中必须明确：网页正文提取不由本插件负责，应交由其他网页抓取或阅读能力处理。
