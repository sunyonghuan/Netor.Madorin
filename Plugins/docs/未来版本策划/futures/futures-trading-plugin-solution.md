# 期货交易插件项目方案（单产品版）

## 1. 目标

插件向 AI 提供交易工具，并通过 WebSocket 与交易软件进行一对一通信。
场景固定为单产品交易，聚焦稳定下单与查询能力。

## 2. Native AOT 风险评估（先行）

方案可按 AOT 友好方式实现，推荐 `ClientWebSocket` + `System.Text.Json` Source Generator。
禁止动态 JSON 路由、反射序列化和运行时类型加载。
结论是可实施，不需要拒绝该方案。

## 3. 场景边界（已确认）

1. 仅一个交易软件实例，一对一通信。
2. 仅一个固定产品，所有接口不需要合约代码。
3. 不做下单前风控、不做限流、不做交易时段控制。
4. 交易软件负责最终风控与执行校验。

## 4. 总体架构

1. Tool Layer：对 AI 暴露交易工具。
2. Ws Gateway：维护 WebSocket 连接、请求响应匹配、超时与重连。
3. Protocol Adapter：标准请求与交易软件协议之间的转换。
4. Audit Log：记录请求、响应、错误和 traceId。

## 5. 工具能力清单

### 5.1 交易操作类

1. 买入
参数：direction（方向）、volume（手数）。

2. 挂单
参数：positionId（持仓ID）、volume（挂单手数）、price（挂单价格）。

3. 撤单
参数：orderId（可选）、price（可选）。
规则：orderId 与 price 都不传时，撤销全部挂单。

### 5.2 查询类

4. 查询持仓
返回持仓列表，至少包含 positionId、direction、volume。

5. 查询最新价格
无参数，直接返回唯一产品当前价格。

6. 回溯窗口（K线）
参数：windowMinutes（回溯分钟数）。
返回窗口内 K 线序列（时间、开高低收、成交量）。

7. 查询挂单
返回当前挂单列表（orderId、positionId、price、volume、status）。

8. 查询账户余额
无参数，返回当前账户余额。

9. 查询盈亏状态
无参数，返回 totalPnl（总盈亏）、floatingPnl（浮动盈亏）、realizedPnl（已实现盈亏）。

## 6. 统一返回结构

所有工具统一返回：success、code、message、data、traceId。
其中 traceId 用于日志追踪与问题复盘。

建议错误码。
1. INVALID_ARGUMENT
2. WS_NOT_CONNECTED
3. UPSTREAM_ERROR
4. ORDER_NOT_FOUND
5. INTERNAL_ERROR

## 7. WebSocket 交互建议

1. 每个请求都生成 requestId，由交易软件回包原样返回。
2. 插件维护 requestId 到任务的映射，超时后返回 UPSTREAM_ERROR。
3. 事件类消息（成交回报、撤单回报）走订阅通道异步推送。

建议保留幂等键字段 clientOrderKey，避免重复点击导致重复下单。

## 8. 分阶段实施

第一阶段：打通连接、价格查询、持仓查询、挂单查询。
第二阶段：完成买入、挂单、撤单及请求回包匹配。
第三阶段：完成账户余额、盈亏状态、K线窗口查询。
第四阶段：补全审计日志、错误码细化和回放测试。

## 9. 验收标准

1. 每个工具至少覆盖成功、参数错误、上游异常三类测试。
2. 撤单需覆盖单单撤销、按价格撤销、全撤三种场景。
3. 盈亏接口必须同时返回总盈亏、浮动盈亏、已实现盈亏。









