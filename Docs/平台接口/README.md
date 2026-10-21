# 平台接口文档

> 整理日期：2026-10-19。
> 源码基准：`4c54b4c2be1b4cc9e519d3c51c554038b29d53fc`。
> 技术版本：.NET 10 / ASP.NET Core Minimal API。
> 本文记录当前程序已经实现的 HTTP 接口及数据契约，不将策划中的能力视为已实现接口。

本目录是 `Netor.Cortana.Platform.Api` 当前已实现接口的参考文档，按功能拆分维护，共覆盖 **29 个 HTTP 路由、35 个数据模型、213 个 DTO 字段**。原 `Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Api/API接口文档.md` 的内容已迁入本目录。

先阅读[通用约定](./00-通用约定.md)，再按功能查看接口、参数字段表和示例。共享模型保留一处完整定义，并通过链接引用；原始接口编号予以保留。

[返回文档中心](../README.md)

## 功能文档

| 文档 | 接口数 | 内容 |
| --- | --- | --- |
| [00-通用约定](./00-通用约定.md) | - | 通信与认证、JSON 类型、错误体、枚举、契约边界、源码依据及验证维护。 |
| [01-健康检查与账号](./01-健康检查与账号.md) | 3 | 健康检查、账号登录、当前账号及令牌获取。 |
| [02-资源市场](./02-资源市场.md) | 3 | 非版本化市场列表、分页资源、资源详情、版本和收费方案。 |
| [03-订阅与授权](./03-订阅与授权.md) | 4 | 订阅资源、查询全部订阅、取消订阅和有效授权。 |
| [04-订单与支付](./04-订单与支付.md) | 5 | 创建订单、个人订单列表及别名、订单详情和当前模拟支付。 |
| [05-资源下载与记录](./05-资源下载与记录.md) | 3 | GET/POST 资源包下载、下载授权、副作用和下载记录。 |
| [06-客户端安装同步与更新](./06-客户端安装同步与更新.md) | 3 | 安装清单、账号资源同步和客户端更新检查。 |
| [07-技能接口](./07-技能接口.md) | 2 | 技能专用清单和下载接口，以及与通用资源接口的差异。 |
| [08-创作者与结算](./08-创作者与结算.md) | 6 | 创作者申请与资料、ZIP 包上传、资源提交、个人资源和结算。 |

## 接口清单

点击路由进入对应功能说明。“登录”表示必须携带[认证请求头](./00-通用约定.md#22-身份认证)；响应中的嵌套模型通过字段表展开。

| 编号 | 方法 | 完整路由 | 登录 | 请求 | 200 响应 |
| --- | --- | --- | --- | --- | --- |
| 01 | GET | [`/api/health`](./01-健康检查与账号.md#01-健康检查) | 否 | 无 | [`ApiHealthResponse`](./01-健康检查与账号.md#apihealthresponse) |
| 02 | GET | [`/api/market/assets`](./02-资源市场.md#02-非版本化市场列表) | 否 | 无 | [`MarketAssetListItem[]`](./02-资源市场.md#marketassetlistitem) |
| 03 | POST | [`/api/v1/auth/login`](./01-健康检查与账号.md#03-账号登录) | 否 | [`ApiLoginRequest`](./01-健康检查与账号.md#apiloginrequest) | [`ApiLoginResponse`](./01-健康检查与账号.md#apiloginresponse) |
| 04 | GET | [`/api/v1/me`](./01-健康检查与账号.md#04-当前账号) | 是 | 无 | [`ApiAccountResponse`](./01-健康检查与账号.md#apiaccountresponse) |
| 05 | GET | [`/api/v1/assets`](./02-资源市场.md#05-分页资源列表) | 否 | Query：`page`、`pageSize`、`type?` | [`ApiAssetPageResponse`](./02-资源市场.md#apiassetpageresponse) |
| 06 | GET | [`/api/v1/assets/{idOrSlug}`](./02-资源市场.md#06-资源详情) | 否 | Path：`idOrSlug` | [`ApiAssetDetail`](./02-资源市场.md#apiassetdetail) |
| 07 | GET | [`/api/v1/assets/{assetId}/download`](./05-资源下载与记录.md#07-通用资源下载) | 是 | Path：`assetId` | ZIP binary |
| 08 | POST | [`/api/v1/assets/{assetId}/downloads`](./05-资源下载与记录.md#08-post-方式下载资源) | 是 | Path：`assetId`；无请求体 | ZIP binary |
| 09 | GET | [`/api/v1/entitlements`](./03-订阅与授权.md#09-有效授权列表) | 是 | 无 | [`ApiEntitlement[]`](./03-订阅与授权.md#apientitlement) |
| 10 | GET | [`/api/v1/client/install/{assetIdOrSlug}`](./06-客户端安装同步与更新.md#10-客户端安装清单) | 是 | Path：`assetIdOrSlug` | [`ApiClientInstallManifest`](./06-客户端安装同步与更新.md#apiclientinstallmanifest) |
| 11 | GET | [`/api/v1/client/sync`](./06-客户端安装同步与更新.md#11-客户端同步) | 是 | 无 | [`ApiClientSyncResponse`](./06-客户端安装同步与更新.md#apiclientsyncresponse) |
| 12 | POST | [`/api/v1/client/updates`](./06-客户端安装同步与更新.md#12-客户端更新检查) | 是 | [`ApiClientUpdateCheckRequest`](./06-客户端安装同步与更新.md#apiclientupdatecheckrequest) | [`ApiClientUpdateCheckResponse`](./06-客户端安装同步与更新.md#apiclientupdatecheckresponse) |
| 13 | POST | [`/api/v1/creator/apply`](./08-创作者与结算.md#13-申请或更新创作者资料) | 是 | [`ApiCreatorApplyRequest`](./08-创作者与结算.md#apicreatorapplyrequest) | [`ApiCreatorProfileResponse`](./08-创作者与结算.md#apicreatorprofileresponse) |
| 14 | GET | [`/api/v1/creator/profile`](./08-创作者与结算.md#14-查询创作者资料) | 是 | 无 | [`ApiCreatorProfileResponse`](./08-创作者与结算.md#apicreatorprofileresponse) |
| 15 | POST | [`/api/v1/creator/packages`](./08-创作者与结算.md#15-上传创作者资源包) | 是，创作者已通过 | multipart 表单 | [`ApiCreatorPackageUploadResponse`](./08-创作者与结算.md#apicreatorpackageuploadresponse) |
| 16 | POST | [`/api/v1/creator/assets`](./08-创作者与结算.md#16-提交创作者资源) | 是，创作者已通过 | [`ApiCreatorAssetSubmitRequest`](./08-创作者与结算.md#apicreatorassetsubmitrequest) | [`ApiCreatorAssetSubmitResponse`](./08-创作者与结算.md#apicreatorassetsubmitresponse) |
| 17 | GET | [`/api/v1/creator/assets`](./08-创作者与结算.md#17-我的创作者资源) | 是 | 无 | [`ApiCreatorAssetItem[]`](./08-创作者与结算.md#apicreatorassetitem) |
| 18 | GET | [`/api/v1/creator/settlements`](./08-创作者与结算.md#18-我的创作者结算) | 是 | 无 | [`ApiCreatorSettlementItem[]`](./08-创作者与结算.md#apicreatorsettlementitem) |
| 19 | GET | [`/api/v1/me/subscriptions`](./03-订阅与授权.md#19-我的订阅) | 是 | 无 | [`ApiSubscriptionItem[]`](./03-订阅与授权.md#apisubscriptionitem) |
| 20 | POST | [`/api/v1/subscriptions/{subscriptionId}/cancel`](./03-订阅与授权.md#20-取消订阅) | 是 | Path：`subscriptionId`；无请求体 | [`ApiCancelSubscriptionResponse`](./03-订阅与授权.md#apicancelsubscriptionresponse) |
| 21 | GET | [`/api/v1/me/downloads`](./05-资源下载与记录.md#21-我的下载记录) | 是 | 无 | [`ApiDownloadRecord[]`](./05-资源下载与记录.md#apidownloadrecord) |
| 22 | POST | [`/api/v1/assets/{assetId}/subscribe`](./03-订阅与授权.md#22-订阅指定资源) | 是 | Path：`assetId`；[`ApiCreateSubscriptionRequest`](./03-订阅与授权.md#apicreatesubscriptionrequest) | [`ApiSubscribeResponse`](./03-订阅与授权.md#apisubscriberesponse) |
| 23 | POST | [`/api/v1/orders`](./04-订单与支付.md#23-创建订单或免费订阅) | 是 | [`ApiCreateOrderRequest`](./04-订单与支付.md#apicreateorderrequest) | [`ApiSubscribeResponse`](./03-订阅与授权.md#apisubscriberesponse) |
| 24 | GET | [`/api/v1/me/orders`](./04-订单与支付.md#24-我的订单) | 是 | 无 | [`ApiOrderListItem[]`](./04-订单与支付.md#apiorderlistitem) |
| 25 | GET | [`/api/v1/orders`](./04-订单与支付.md#25-订单列表别名) | 是 | 无 | [`ApiOrderListItem[]`](./04-订单与支付.md#apiorderlistitem) |
| 26 | GET | [`/api/v1/orders/{orderId}`](./04-订单与支付.md#26-订单详情) | 是 | Path：`orderId` | [`ApiOrderDetail`](./04-订单与支付.md#apiorderdetail) |
| 27 | POST | [`/api/v1/orders/{orderId}/pay`](./04-订单与支付.md#27-支付订单当前为模拟支付) | 是 | Path：`orderId`；无请求体 | [`ApiPayOrderResponse`](./04-订单与支付.md#apipayorderresponse) |
| 28 | GET | [`/api/v1/skills/{assetId}/manifest`](./07-技能接口.md#28-技能清单) | 是 | Path：`assetId` | [`ApiSkillManifest`](./07-技能接口.md#apiskillmanifest) |
| 29 | GET | [`/api/v1/skills/{assetId}/download`](./07-技能接口.md#29-技能下载) | 是 | Path：`assetId` | ZIP binary |

## 数据模型索引

各模型的字段名、C# 类型、JSON 类型和空值约束均在对应功能文档中定义。

| 功能 | 模型 |
| --- | --- |
| [健康检查与账号](./01-健康检查与账号.md) | [`ApiHealthResponse`](./01-健康检查与账号.md#apihealthresponse)、[`ApiLoginRequest`](./01-健康检查与账号.md#apiloginrequest)、[`ApiLoginResponse`](./01-健康检查与账号.md#apiloginresponse)、[`ApiAccountResponse`](./01-健康检查与账号.md#apiaccountresponse) |
| [资源市场](./02-资源市场.md) | [`MarketAssetListItem`](./02-资源市场.md#marketassetlistitem)、[`ApiAssetPageResponse`](./02-资源市场.md#apiassetpageresponse)、[`ApiAssetListItem`](./02-资源市场.md#apiassetlistitem)、[`ApiAssetDetail`](./02-资源市场.md#apiassetdetail)、[`ApiAssetVersion`](./02-资源市场.md#apiassetversion)、[`ApiPricingPlan`](./02-资源市场.md#apipricingplan) |
| [订阅与授权](./03-订阅与授权.md) | [`ApiCreateSubscriptionRequest`](./03-订阅与授权.md#apicreatesubscriptionrequest)、[`ApiSubscribeResponse`](./03-订阅与授权.md#apisubscriberesponse)、[`ApiSubscriptionItem`](./03-订阅与授权.md#apisubscriptionitem)、[`ApiCancelSubscriptionResponse`](./03-订阅与授权.md#apicancelsubscriptionresponse)、[`ApiEntitlement`](./03-订阅与授权.md#apientitlement) |
| [订单与支付](./04-订单与支付.md) | [`ApiCreateOrderRequest`](./04-订单与支付.md#apicreateorderrequest)、[`ApiOrderListItem`](./04-订单与支付.md#apiorderlistitem)、[`ApiOrderDetail`](./04-订单与支付.md#apiorderdetail)、[`ApiPayOrderResponse`](./04-订单与支付.md#apipayorderresponse) |
| [资源下载与记录](./05-资源下载与记录.md) | [`ApiDownloadRecord`](./05-资源下载与记录.md#apidownloadrecord) |
| [客户端安装同步与更新](./06-客户端安装同步与更新.md) | [`ApiClientInstallManifest`](./06-客户端安装同步与更新.md#apiclientinstallmanifest)、[`ApiClientInstallVersion`](./06-客户端安装同步与更新.md#apiclientinstallversion)、[`ApiClientSyncResponse`](./06-客户端安装同步与更新.md#apiclientsyncresponse)、[`ApiClientUpdateCheckRequest`](./06-客户端安装同步与更新.md#apiclientupdatecheckrequest)、[`ApiClientInstalledAsset`](./06-客户端安装同步与更新.md#apiclientinstalledasset)、[`ApiClientUpdateCheckResponse`](./06-客户端安装同步与更新.md#apiclientupdatecheckresponse)、[`ApiClientUpdateItem`](./06-客户端安装同步与更新.md#apiclientupdateitem) |
| [技能接口](./07-技能接口.md) | [`ApiSkillManifest`](./07-技能接口.md#apiskillmanifest) |
| [创作者与结算](./08-创作者与结算.md) | [`ApiCreatorApplyRequest`](./08-创作者与结算.md#apicreatorapplyrequest)、[`ApiCreatorProfileResponse`](./08-创作者与结算.md#apicreatorprofileresponse)、[`ApiCreatorPackageUploadResponse`](./08-创作者与结算.md#apicreatorpackageuploadresponse)、[`ApiCreatorAssetSubmitRequest`](./08-创作者与结算.md#apicreatorassetsubmitrequest)、[`ApiCreatorAssetSubmitResponse`](./08-创作者与结算.md#apicreatorassetsubmitresponse)、[`ApiCreatorAssetItem`](./08-创作者与结算.md#apicreatorassetitem)、[`ApiCreatorSettlementItem`](./08-创作者与结算.md#apicreatorsettlementitem) |

## 维护规则

- 本目录只记录当前已实现的接口；策划、执行计划仍按文档中心的对应主题管理。
- 路由、参数、返回值或业务规则变化时，同步更新对应功能文档及本页接口清单。
- 共享模型在所属功能文档中维护完整字段表，其他文档使用相对链接引用；新增功能文档时同步更新功能目录和数据模型索引。
- 通用序列化、认证、错误或枚举约定发生变化时，同步更新[通用约定](./00-通用约定.md)。
