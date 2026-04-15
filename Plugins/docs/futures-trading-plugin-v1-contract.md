# 期货交易插件 V1 接口契约（单产品版）

## 1. 文档定位

本文档定义期货交易插件 V1 对 AI 公开的工具契约。
用于统一开发、联调、测试和后续兼容策略。

## 2. 范围与边界

V1 包含买入、挂单、撤单、查询持仓、查询挂单、查询价格、查询余额、查询盈亏和 K 线窗口回溯。
场景固定为单产品，接口不包含合约代码。
插件不做下单前校验、不做限流、不做交易时段控制。

## 3. Native AOT 约束

所有消息模型必须使用强类型类或记录。
所有 JSON 序列化与反序列化必须使用 Source Generator 上下文。
禁止匿名类型序列化、dynamic 路由与反射序列化。

## 4. 公共请求上下文

所有工具支持以下公共字段。
wsEndpoint：交易软件 WebSocket 地址。
requestTimeoutMs：请求超时毫秒数，默认 10000。
operationId：调用追踪标识，可选。

## 5. 统一返回结构

统一返回 success、code、message、data、traceId。
traceId 用于日志定位与联调追踪。

## 6. 错误码契约

INVALID_ARGUMENT：参数缺失或格式错误。
WS_NOT_CONNECTED：交易软件连接不可用。
UPSTREAM_ERROR：上游交易软件返回失败。
ORDER_NOT_FOUND：指定订单不存在。
INTERNAL_ERROR：插件内部未分类错误。

## 7. 工具契约明细

### 7.1 trade_buy

用途：提交买入请求。
必填参数：direction、volume。
direction 表示方向，volume 表示手数。
成功返回 data.orderId、data.status。

### 7.2 trade_place_order

用途：基于持仓挂单。
必填参数：positionId、volume、price。
成功返回 data.orderId、data.positionId、data.status。

### 7.3 trade_cancel_order

用途：撤销订单。
可选参数：orderId、price。
当 orderId 与 price 均缺省时，执行全撤。
成功返回 data.cancelledCount、data.failedItems。

### 7.4 trade_get_positions

用途：查询当前持仓。
参数：无。
成功返回 data.items，项包含 positionId、direction、volume。

### 7.5 trade_get_pending_orders

用途：查询当前挂单。
参数：无。
成功返回 data.items，项包含 orderId、positionId、price、volume、status。

### 7.6 trade_get_latest_price

用途：查询单产品最新价格。
参数：无。
成功返回 data.price、data.timestamp。

### 7.7 trade_get_kline_window

用途：查询最近窗口 K 线数据。
必填参数：windowMinutes。
成功返回 data.items，项包含 time、open、high、low、close、volume。

### 7.8 trade_get_account_balance

用途：查询当前账户余额。
参数：无。
成功返回 data.balance、data.currency。

### 7.9 trade_get_pnl_status

用途：查询当前盈亏状态。
参数：无。
成功返回以下字段为必返。
1. data.totalPnl
2. data.floatingPnl
3. data.realizedPnl

## 8. WebSocket 交互要求

每次请求必须携带 requestId，并由上游回包原样返回。
插件必须维护 requestId 与待完成任务映射。
超时场景返回 UPSTREAM_ERROR 并包含 traceId。

## 9. 日志与审计

每次调用记录 time、toolName、operationId、requestId、success、code、traceId。
关键交易动作额外记录 orderId 与耗时。
日志不记录敏感凭据。

## 10. 验收标准

每个工具至少覆盖成功、参数错误、上游异常三类测试。
撤单工具覆盖按订单撤单、按价格撤单、全撤三种场景。
盈亏工具必须返回总盈亏、浮动盈亏、已实现盈亏。

## 11. 建议实施顺序

第一步实现连接管理、价格查询、持仓查询、挂单查询。
第二步实现买入、挂单、撤单。
第三步实现余额、盈亏、K 线窗口。
第四步完善审计日志与回放测试。
