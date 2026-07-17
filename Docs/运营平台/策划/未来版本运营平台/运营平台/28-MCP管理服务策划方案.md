# 28 - 后台 MCP 管理服务策划方案

> 目标：在 `Netor.Cortana.Platform.Admin` 内嵌一套 HTTP 模式的 MCP（Model Context Protocol）服务，让 AI 智能体可以以"管理员"身份调用后台已有的运营能力（账户、资源、订阅、订单、审核、结算、设置）；每个管理员账户拥有独立的 MCP 令牌，可在后台自助生成、查看（仅一次）、轮换、吊销。

> 代码核对（2026-06-12）：当前代码仅落地账户 / 资源 / 仪表盘相关 MCP 能力、令牌、鉴权、最小审计、幂等和限流；订阅 / 订单 / 审核 / 结算 / 设置等完整后台运营工具链与测试仍未完成。本策划继续保留在未来版本策划，配套执行计划见 [29-执行计划-MCP管理服务接入.md](29-执行计划-MCP管理服务接入.md)。

## 1. 背景与边界

- 现状：[Netor.Cortana.Platform.Admin/Program.cs](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Program.cs) 使用 Cookie 鉴权 + MVC + Layui，`FallbackPolicy = RequireAuthenticatedUser`，所有管理动作通过 Controller + EF Core (`PlatformDbContext`) 完成。
- 复用：业务能力直接复用 [PlatformDbContext](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Entitys/Data/PlatformDbContext.cs) 与 `Netor.Cortana.Platform.Services` 中已有的 Service（`PackageStorageService`、`OrderService`、`SubscriptionService`、`CreatorService`、`DownloadService` 等），不重复实现业务规则。
- 不在范围：
  - 普通用户（`Account`）侧的 MCP；用户侧令牌已有 `Netor.Cortana.Platform.Api` 提供，本次不动。
  - MCP Stdio / SSE 模式；本次只交付 Streamable HTTP（HTTP POST + 可选 GET 流）。
  - 自然语言到工具的编排策略，由调用方智能体决定。

## 2. 总体架构

```text
┌─────────────────────────────────────────────────────┐
│  AI Agent (Claude Desktop / Custom / 内部桌面端)      │
│           ↓  MCP over HTTP (Bearer Token)            │
│           POST /mcp   Mcp-Session-Id 头              │
└─────────────────────────────────────────────────────┘
                         │
┌────────────────────────┴────────────────────────────┐
│  Netor.Cortana.Platform.Admin (Kestrel, net10.0)     │
│                                                      │
│  ┌─────────────────────┐   ┌─────────────────────┐   │
│  │ MVC Pipeline (Cookie)│   │ MCP Pipeline (Token) │   │
│  │  /Accounts /Assets  │   │  /mcp                │   │
│  │  /Settings ...      │   │                     │   │
│  └─────────┬───────────┘   └──────────┬──────────┘   │
│            │                          │              │
│            ▼                          ▼              │
│      ┌─────────────────────────────────────┐         │
│      │ AdminOperations (共享应用服务层，    │         │
│      │   Controller 与 MCP Tool 都调用它)   │         │
│      └─────────────────────────────────────┘         │
│            │                                          │
│            ▼                                          │
│  PlatformDbContext + Netor.Cortana.Platform.Services  │
└──────────────────────────────────────────────────────┘
```

关键决策：

1. **同进程同端口**：MCP 与 MVC 共享 `WebApplication`，但单独路径前缀 `/mcp`，单独 Auth Scheme。
2. **统一应用服务层**：把每个 Controller 内目前以 `dbContext.*` 方式拼出的查询/命令逻辑，下沉到一组 `*Operations` 服务（例如 `AccountOperations`、`AssetOperations`），Controller 与 MCP Tool 同时使用，避免 MCP 直接访问 DbContext 又写一遍业务规则。
3. **协议库**：使用官方 [`ModelContextProtocol`](https://github.com/modelcontextprotocol/csharp-sdk) + `ModelContextProtocol.AspNetCore`（NuGet 上的 `0.x` 预览包）。该 SDK 由 Microsoft 与 Anthropic 共同维护，提供 `MapMcp()` 扩展、`McpServerToolType` / `McpServerTool` 特性、Streamable HTTP 传输与会话管理。
4. **传输**：仅启用 Streamable HTTP（POST `/mcp` + 可选 `text/event-stream` 升级）。SSE 旧路径不开。
5. **隔离**：MCP 处理逻辑放到子目录 [Mcp/](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Mcp/)，包括：
   - `AdminMcpAuthHandler`（基于 Bearer Token 的 `AuthenticationHandler`）
   - `Tools/*Tool.cs`（按业务域拆分的工具集合）
   - `Contracts/*.cs`（工具入参出参 DTO，全部 `record` + `JsonSerializerContext`）

## 3. 鉴权设计

### 3.1 数据模型（采用独立表，不复用 ManagerProperty）

> v0.1 草案曾打算把令牌数据塞进 [ManagerProperty](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Entitys/Tables/Managers/ManagerProperty.cs)。复盘后否决：键值表无法支持多令牌、轮换重叠、IP/UA 审计；散落 `mcp.token.*` 行写入也很难做"原子撤销"。改为独立表。

新建实体 `Netor.Cortana.Platform.Entitys/Tables/Managers/ManagerMcpToken`：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `ID` | `string` | 主键（继承 `EntityBase`，沿用平台 Snowflake/Guid 约定） |
| `ManagerID` | `string` | 外键 → `Manager.ID`，索引 |
| `TokenHash` | `string(64)` | `Base64Url(SHA256(token))`，**唯一索引** |
| `TokenPrefix` | `string(12)` | 明文前 8 位（`mcp_xxxx…`），仅供 UI 辨认 |
| `Note` | `string(64)` | 备注（来源/用途） |
| `Enabled` | `bool` | 临时禁用而不删除 |
| `CreatedUtc` | `DateTimeOffset` | 创建时间 |
| `LastUsedUtc` | `DateTimeOffset?` | 最近一次成功调用时间 |
| `LastUsedIp` | `string?(64)` | 最近一次成功调用 IP（脱敏存原值，导出时再脱敏） |
| `LastUsedUa` | `string?(256)` | 最近一次成功调用 User-Agent |

效益：

- 一个管理员可拥有多枚令牌（不同智能体/客户端独立追溯，轮换有重叠期）。
- 校验时 `WHERE TokenHash = <hash>` 走唯一索引，O(1)。
- "撤销 = 删一行 / 禁用一行"，无散行问题。

> 选择存哈希而非明文：行业 API Key 实践；丢失只能轮换，不能"看回明文"。

### 3.2 令牌格式

- 生成：`mcp_{base64url(32 字节加密随机)}`，约 47 字符。
- 校验流程：
  1. HTTP `Authorization: Bearer mcp_xxx` 取出。
  2. 服务端 `SHA256` 后到 `ManagerMcpToken` 查找。
  3. 命中且 `Enabled=true` → 联表查 [Manager](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Entitys/Tables/Managers/Manager.cs)，校验 **`Manager.Status == 0`（正常）**。
  4. 注入 `ClaimsPrincipal`（`ManagerID` / `ManagerNo` / `LoginUserName` / `Role.Name` / `Role.Power` / `TokenID` / `AuthMethod=AdminMcp`）。
  5. 异步 `UPDATE LastUsedUtc/LastUsedIp/LastUsedUa`，不阻塞响应。
- 时间常数比较：`CryptographicOperations.FixedTimeEquals`，与 [ApiTokenService](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Api/Security/ApiTokenService.cs) 一致。
- 不带过期：管理员主动轮换；后续若需，加 `ExpiresUtc` 列即可。

> ⚠️ 安全要点：必须二次校验 `Manager.Status`。如果不校验，禁用某管理员账号但未撤销其令牌时，他仍能通过 MCP 操作整个后台。

### 3.3 认证管道

```csharp
builder.Services.AddAuthentication() // 已有 Cookie
    .AddScheme<AuthenticationSchemeOptions, AdminMcpAuthHandler>("AdminMcp", _ => { });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build(); // 保持 MVC 现状

    options.AddPolicy("AdminMcp", policy => policy
        .AddAuthenticationSchemes("AdminMcp")
        .RequireAuthenticatedUser());
});

app.MapMcp("/mcp").RequireAuthorization("AdminMcp").RequireRateLimiting("AdminMcpPerToken");
```

- MVC 仍走 Cookie，未变。
- `/mcp` 仅 `AdminMcp` Scheme 通过；不会与登录跳转冲突。
- 速率限制：内置 `AddRateLimiter`，对 `/mcp` 配 `FixedWindow`（默认 60 req/min/Token），分区键用 `TokenID`（不是 ManagerID，避免一个管理员的两个智能体相互打架）。

### 3.4 角色权限分级

[Manager.Role](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Entitys/Tables/Managers/ManagerRole.cs) 继承 `RoleBase`，已有 `Power`（位标志）字段。MCP 工具须按敏感度声明所需 Power：

| 敏感度 | 适用工具 | 所需角色 |
| --- | --- | --- |
| 只读 | `*.search` / `*.get` / `dashboard.summary` | 任意启用角色 |
| 一般写 | `assets.set_featured` / `categories.create` | 一般运营+ |
| 高敏写 | `accounts.set_status` / `accounts.recharge_wallet` / `reviews.approve` / `settlements.mark_paid` / `accounts.reset_password` | 仅超级管理员 |
| 受保护 | `settings.update` | 仅超级管理员（且阶段 4 才开放） |

实现：`AdminMcpContext.RequireRole(RoleRequirement)`，校验失败返回 `errorCode=FORBIDDEN`。MVP 阶段先简化为"超级管理员（`Power == SuperAdmin`）才能调写工具"，普通角色只读。

### 3.5 幂等性（写工具必备）

LLM 在网络抖动 / 上下文混乱时会重试同一调用。`accounts.recharge_wallet`、`subscriptions.cancel`、`reviews.approve`、`settlements.mark_paid`、`accounts.reset_password` 等**非幂等**——重试就重复扣款 / 重复批通过。

**约束**：所有"写工具"入参统一带可选字段：

```jsonc
{
  "requestId": "string?",   // 客户端生成的 UUID v7 / ULID
  // ...业务参数
}
```

服务端落表 `IdempotencyRecord`（或复用审计表，详见 §4.6）：

| 字段 | 说明 |
| --- | --- |
| `Hash` | `SHA256(ManagerID + ToolName + RequestId)` 唯一索引 |
| `ResultJson` | 上次返回的序列化结果 |
| `CreatedUtc` | 用于过期清理（默认 24h） |

匹配命中 → 直接回放上次结果，`errorCode=IDEMPOTENT_REPLAY`（仍 200）。
未传 `requestId` → 不去重，但工具描述里强提示"建议传 requestId 以避免重复执行"。

### 3.6 后台管理员"我的 MCP 令牌"页面

新增：

- `Controllers/ProfileController.cs`：`GET /Profile/Mcp` 列出当前管理员的令牌（多行，前缀、备注、状态、最近使用 IP/UA/时间）。
- `POST /Profile/Mcp/Generate`（带 `note`）：生成新令牌；明文 TempData 一次性显示，刷新后消失。
- `POST /Profile/Mcp/Revoke?id=...`：删除指定令牌。
- `POST /Profile/Mcp/Toggle?id=...`：切换 `Enabled`。
- 视图 [Views/Profile/Mcp.cshtml](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Views/Profile/Mcp.cshtml)：Layui 风格。
- 在 [_NavPartial.cshtml](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Views/Shared/_NavPartial.cshtml) 顶部右侧个人下拉里加一项"我的 MCP 令牌"。

**一次性明文 UX**：

- 生成后大字号展示，附"复制到剪贴板"按钮（`navigator.clipboard.writeText`）。
- 二次确认"我已妥善保存"复选框，未勾选不能离开页面（`beforeunload`）。
- 红色强提示"页面刷新或离开后明文立即销毁，丢失只能撤销重新生成"。

> 暂不做"超级管理员代他人重置令牌"。当前 Admin 还没有 Managers 列表页，本次也不补；保持最小变更。

## 4. 工具集设计

按业务域拆分。每类挂在一个 `[McpServerToolType]` 类下。所有写操作都要求当前管理员角色具备相应 `Power` 且令牌 `Enabled=true`，统一返回 `OperationResult<T>`：`{ success, errorCode?, message?, data? }`。

### 4.1 命名约定

- **Tool 名一律 `snake_case`，分隔符 `_`，不用 `.`**——MCP 协议虽允许 `.`，但部分客户端历史上对 `.` 处理有 bug；`accounts_search` 比 `accounts.search` 兼容性更好。
- 形式：`{domain}_{verb}` 或 `{domain}_{verb}_{object}`。

### 4.2 阶段 1（MVP，仅只读 + 安全可逆轻量写）

> ⚠️ `settings_update` 移出 MVP（见 §4.6）。`accounts_recharge_wallet` 同步引入幂等字段（见 §3.5）。

| 域 | 工具 | 对应 Controller / 来源 |
| --- | --- | --- |
| 平台总览 | `dashboard_summary` | [HomeController.Index](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Controllers/HomeController.cs) |
| 账户 | `accounts_search` / `accounts_get` / `accounts_set_status` / `accounts_recharge_wallet` / `accounts_list_transactions` | [AccountsController](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Controllers/AccountsController.cs) |
| 资源 | `assets_search` / `assets_get` / `assets_set_featured` / `assets_set_status` | `AssetsController` |
| 分类 | `categories_list` / `categories_create` / `categories_update` | `CategoriesController` |
| 订阅 | `subscriptions_search` / `subscriptions_cancel` | `SubscriptionsController` |
| 订单 | `orders_search` / `orders_get` / `orders_list_transactions` | `OrdersController` |
| 设置 | `settings_list` / `settings_get` | [SettingsController](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Controllers/SettingsController.cs) |

### 4.3 阶段 2（涉及风控 / 资金 / 配置的高敏写）

| 域 | 工具 |
| --- | --- |
| 资源审核 | `reviews_list_pending` / `reviews_approve` / `reviews_reject` |
| 创作者 | `creators_search` / `creators_approve` / `creators_suspend` |
| 结算 | `settlements_search` / `settlements_mark_paid` / `settlements_add_note` |
| 账户 | `accounts_reset_password`（强制要求传"操作原因"，写审计日志） |
| 设置 | `settings_update`（仅超级管理员；强制白名单与审计） |

### 4.4 阶段 3（管理员、令牌自管，可选）

`profile_mcp_list_tokens` / `profile_mcp_rotate`（智能体自助管理令牌，意义不大，留作后续）。

### 4.5 工具元数据与响应规范

- 入参出参均为 `record`，必填字段加 `[JsonRequired]`，可空字段保持 `?`。
- 每个工具的 `Description` 用中文，写清"语义、副作用、所需角色、典型用法、错误码、是否幂等"。
- 分页：MCP 一律使用"列表精简版 + 详情完整版"两层结构：
  - 列表（`*_search` / `*_list`）：`pageSize` **上限 20**（不再是 Controller 的 100），返回字段精简（id/name/key 等核心标识 + 1~2 个状态字段），减少 LLM 上下文消耗。
  - 详情（`*_get`）：返回完整字段。
- 高级筛选：MCP 工具签名只暴露 5~6 个主参数（`keyword/status/page/pageSize/orderBy/order`），更细的二十多个 Controller 参数收进可选 `filter` 子对象，并在 description 里说明字段集合。避免一个工具的 schema 上百字段把 LLM 选择空间淹没。
- 时间统一 ISO8601 字符串（UTC）。
- 金额用 `decimal`，由 SDK 默认序列化（不强制字符串以保持习惯）。
- 写工具入参带可选 `requestId`（见 §3.5 幂等）。

### 4.6 settings_update 推迟到阶段 4 的理由

[SettingsController](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Controllers/SettingsController.cs) 的 `IsProtection=true` 只挡住一部分（密钥类）。`package.storage.s3.bucket`、`package.storage.publicBaseUrl`、`platform.download.anonymous`、`platform.storage.root` 等关键开关并未受保护，但 LLM 改这些等于把生产配置交给智能体。

约束：

- MVP（阶段 1）只暴露 `settings_list` / `settings_get`。
- `settings_update` 在阶段 4 与审计同时上线，并且：
  - 仅超级管理员 Power。
  - 服务端维护一份"MCP 可写 Key 白名单"（默认仅 `resource.featured.limit` / `download.daily.limit` / `notification.*` 等运营级开关），白名单外一律拒绝。
  - 全部更新强制写审计 + 必填 `reason` 字段。

### 4.7 历史缺陷顺手修：充值流水缺失

阶段 2 抽 `AccountOperations.RechargeAsync` 时一并修掉以下缺陷：

> 当前 [AccountsController.Recharge:715](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Controllers/AccountsController.cs#L715) 直接 `wallet.Money += amount` 后 `SaveChanges`，**未写 `Transaction` 流水**。Controller 与 MCP 共用 Operations 后，若不修，智能体充值会把这个缺陷放大、更难追溯。

修法：`RechargeAsync` 内同一 `SaveChangesAsync` 同时新建 `Transaction`（金额、操作人、来源标记 `MCP` 或 `MVC`、`requestId` 透传）。Controller 的 `Recharge` 行为不变，由 Operations 内部统一处理。

### 4.8 审计前置（不再推迟到阶段 4）

> 复盘修正：v0.1 草案把审计推到阶段 4。但阶段 3 已暴露 `accounts_set_status` / `assets_set_featured` / `accounts_recharge_wallet` 等写工具，没有审计就裸奔。改为审计与 MCP 同步上线。

阶段拆分：

- **阶段 1 末（鉴权完成时）**：上"最小审计 + 落库"。
  - 新建 `Tables/Managers/ManagerAuditLog`：`ID/ManagerID/TokenID?/ToolName/Source(MCP|MVC)/IP/UA/Success/ErrorCode?/DurationMs/CreatedUtc`。
  - **不存入参/出参**，体积可控、上线门槛低。
- **阶段 4**：补完整版。
  - 增加 `PayloadJson`（脱敏后）/ `ResultJson`（脱敏后）/ `Reason`（写工具必填）。
  - 在工具上挂 `IMcpServerToolFilter`（或 SDK 等价拦截点）统一写入。
  - 脱敏字段：密码、SafePassword、Token 全字段、Settings 中 `IsProtection=true` 的 Value。

### 4.9 错误码字典

`OperationResult.errorCode` 统一字典：

| Code | 含义 |
| --- | --- |
| `NOT_FOUND` | 实体不存在 |
| `FORBIDDEN` | 角色 Power 不足 |
| `VALIDATION` | 入参校验失败 |
| `RATE_LIMITED` | 触发限流（一般由 429 返回，工具层不会出现） |
| `PROTECTED` | 命中 `IsProtection=true` 或受保护资源 |
| `CONFLICT` | 状态冲突（订阅已取消、订单已支付等） |
| `IDEMPOTENT_REPLAY` | `requestId` 命中，回放上次结果（仍 `success=true`） |
| `INTERNAL` | 未预期异常（同时记日志） |

### 4.10 仅暴露 Tools，不暴露 Resources / Prompts

本期 MCP 服务**只实现 Tools**，不开放 Resources（让 LLM 浏览数据集合，如直接读账户列表）和 Prompts（预定义会话模板）。

- 安全：Resources 一旦开放等于"开放数据库的批量只读视图"，与最小权限原则冲突。
- 工程：减少初期实现面，便于聚焦工具集合的稳定性。
- 未来：若后续需要 Resources（例如 LLM 主动浏览资源市场），单独立项评估。

## 5. AOT 风险评估（重点）

> 用户特别要求：**注意 AOT 发布风险**。

### 5.1 当前发布形态

- [Build/Platfrom.ps1](Build/Platfrom.ps1) 默认 `Configuration=Release`、`Runtime=linux-x64`、可选 `-SelfContained`，未传 `-p:PublishAot=true`。
- 三个项目（Admin/Api/Web）的 `.csproj` 中 **均未启用 `<PublishAot>` 或 `<PublishTrimmed>`**。
- 因此当前生产是普通 JIT + R2R + 可选 SelfContained，**不存在 AOT 风险**。

### 5.2 是否要为 MCP 启用 AOT？

**结论：不启用，保持现状。**

理由：
1. **EF Core**：Admin 项目通过 [PlatformDbContext](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Entitys/Data/PlatformDbContext.cs) 重度依赖 EF Core。EF Core 10 已支持 NativeAOT，但要求工程开启 [Compiled Models](https://learn.microsoft.com/ef/core/performance/advanced-performance-topics#compiled-models) 与 `EFCore.AOT` 兼容路径，且很多 LINQ 表达式（动态 `EF.Property<string>` 在 `AccountsController` 里大量使用）会触发 AOT 警告。改造成本远高于本次价值。
2. **MVC + Razor**：Admin 大量使用 `Controller`、Tag Helper、Razor 视图（`Views/*.cshtml`）。Razor Views 在 AOT 下并未官方支持，需迁移到 Razor Pages + Static Web Asset 编译，工作量很大。
3. **MCP SDK**：`ModelContextProtocol` SDK 当前仍依赖反射建工具元数据（参数 schema 推导）。虽然 SDK 在向 AOT 友好演进，但 0.x 阶段仍不保证 trimming-safe。
4. **JSON**：MCP 协议 JSON 体可强制使用 `JsonSerializerContext` 源生成，**这一点我们一开始就做**，避免未来切 AOT 时再返工。
5. **客户端 vs 服务端**：本平台真正需要 AOT 的是桌面端（`Netor.Cortana.UI`），服务端运行在 Linux 容器/VM 上，启动时间和体积可接受 JIT。

### 5.3 防御性约束

为了"未来某天"如果决定切 AOT 时改动最小，我们在本次落地时强制：

- ✅ MCP 自定义 DTO 全部进入一个 `AdminMcpJsonContext : JsonSerializerContext`，并通过 `ConfigureHttpJsonOptions` / SDK 选项注入。
- ✅ 不在 MCP 路径上使用 `JsonSerializer.Serialize<object>(...)` / `JsonElement.Deserialize` 等开放泛型反射调用。
- ✅ 不使用 `Activator.CreateInstance` 或基于反射的 DI 工厂，工具类构造参数全部由 DI 容器解析。
- ✅ 所有 Tool 方法签名 `Task<XxxResult>`，避免 `dynamic` / `object` 返回。
- ✅ 在 `.csproj` 中**显式声明** `<IsAotCompatible>false</IsAotCompatible>` 与 `<PublishTrimmed>false</PublishTrimmed>`，并附中文注释说明为何（防止有人无意中加 `<PublishAot>true</PublishAot>` 或 `<PublishTrimmed>true</PublishTrimmed>` 导致发布失败）。
- ✅ 发布脚本不改：仍按 [Build/Platfrom.ps1](Build/Platfrom.ps1) 现有方式打包；CI 中加一条校验，禁止任何 `.csproj` 引入 `PublishAot=true`。

### 5.4 残留风险与处置

| 风险 | 触发场景 | 处置 |
| --- | --- | --- |
| 有人后续误启用 AOT | 误改 csproj | `IsAotCompatible=false` + CI 校验脚本 |
| MCP SDK 大版本升级破坏接口 | 0.x → 1.x | `<PackageReference>` 锁定到具体小版本号；引入 `migrate-mstest-v3-to-v4`-style 升级文档 |
| Source-gen JSON 漏字段 | DTO 加字段忘了挂 `JsonSerializable` | 上线前用集成测试覆盖每个 Tool 的 happy path |
| HMAC SigningKey 被泄露 | 配置外泄 | MCP 不依赖 HMAC（只比 SHA256 哈希），与 Api 解耦；但 Api 端的 SigningKey 仍按既有方式存储 |

## 6. 配置项清单

| Key | 默认值 | 说明 |
| --- | --- | --- |
| `Mcp:Enabled` | **`false`**（生产）/ `true`（开发） | 关闭则 `/mcp` 直接 404；生产**默认关闭**，由运维显式开启 |
| `Mcp:RateLimit:PermitPerMinute` | `60` | 每令牌每分钟请求数 |
| `Mcp:RateLimit:QueueLimit` | `0` | 不排队，超限 429 |
| `Mcp:Cors:AllowedOrigins` | `[]` | 默认不放任何跨域；调用方为后端服务时用不到 |
| `Mcp:MaxRequestBytes` | `1048576` | 1MB，限制单条 MCP 请求体 |
| `Mcp:Logging:Verbose` | `false` | 是否记录工具入参（脱敏后）到结构化日志 |
| `Logging:LogLevel:Netor.Cortana.Platform.Admin.Mcp` | `Information` | 单独控制 MCP 模块的日志级别 |
| `Mcp:Idempotency:RetentionHours` | `24` | `IdempotencyRecord` 保留时长，过期由后台任务清理 |
| `Mcp:Settings:WriteWhitelist` | `[]` | 阶段 4 用：`settings_update` 允许写入的 Key 列表 |

后台 [Settings](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Controllers/SettingsController.cs) 中以 `mcp.*` 前缀展示一组只读说明（保护设置），实际生效以 `appsettings.json` 为准。

## 6.5 部署前提（Deployment Prerequisites）

在生产启用 `/mcp` 前必须确认：

1. **必须 HTTPS**。明文 HTTP 暴露 Bearer Token 等于裸奔。`appsettings.Production.json` 强制 `app.UseHttpsRedirection()` 已开。
2. **反向代理 IP 白名单**：建议 Nginx / ALB 在 `/mcp` 路径上配置允许列表（智能体出口 IP 段），与 `/Auth/Login` 同等保护。
3. **`Mcp:Enabled` 默认 `false`**：上线后由运维通过环境变量或配置中心显式开启，避免新部署即暴露。
4. **健康探活端点**：`/mcp/health` 不鉴权（返回 `{ "status": "ok", "version": "..." }`），供 LB / K8s readinessProbe 使用；不暴露任何业务信息。
5. **令牌泄露应急流程**：管理员发现 Token 外泄 → 后台一键禁用/吊销 → 联系运维查 `ManagerAuditLog` 中该 TokenID 的最近调用 → 必要时回滚。

## 7. 测试与验收

### 7.1 单元测试

`Tests/Netor.Cortana.Platform.Admin.Tests/`（如不存在则建项目骨架）：

- `Mcp/AdminMcpAuthHandlerTests`：未带 Token / 错误 Token / `Enabled=false` / `Manager.Status != 0` → 401；正确 → 注入 Claims。
- `Mcp/AdminMcpTokenServiceTests`：生成 / 哈希校验 / 多令牌共存 / 撤销 / 启用切换。
- `Mcp/IdempotencyTests`：相同 `requestId` 命中回放、过期记录被清理。
- `Mcp/Tools/*ToolTests`：每个 Tool 至少 1 个 happy path + 1 个失败路径（`NOT_FOUND` / `FORBIDDEN` / `VALIDATION` / `PROTECTED` 之一）。
- `Operations/AccountOperationsTests`：充值同时写流水、状态切换。

### 7.2 集成测试（HTTP 层）

`Tests/Netor.Cortana.Platform.Admin.IntegrationTests/`：

- 用 `Microsoft.AspNetCore.Mvc.Testing` + `WebApplicationFactory<Program>` 起内存 Host。
- 验 `POST /mcp` 的 JSON-RPC：`initialize` → `tools/list` → 至少调用 `dashboard_summary` / `accounts_search` / `accounts_set_status`。
- 验未带 Token 401、超频 429、工具返回 `errorCode=NOT_FOUND` 字段格式。
- SDK 升级回归靠这套自动化兜底，不能只靠 mcp-inspector 手测。

### 7.3 端到端（手测）

- `dotnet run` 启动 Admin → 浏览器登录 → `Profile/Mcp` 生成令牌（截图保存）。
- 用 [`mcp-inspector`](https://github.com/modelcontextprotocol/inspector) 连 `http://localhost:<port>/mcp`，逐个工具点过去，确认 schema 与返回结构。
- 验证 SQLite + SqlServer 两种数据库（参考 [PlatformDesignTimeDbContextFactory](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Entitys/Data/PlatformDesignTimeDbContextFactory.cs)）下行为一致。

### 7.4 数据库迁移

新建 `ManagerMcpToken`（阶段 1）、`ManagerAuditLog`（阶段 1 末最小版 / 阶段 4 完整版）、`IdempotencyRecord`（阶段 3）：

- `dotnet ef migrations add <Name> --project Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Entitys --startup-project Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Api`。
- 重新生成 [CompiledModels/](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Entitys/Data/CompiledModels)（已启用 Compiled Models 源生成）。
- SqlServer + Sqlite 双方言 `dotnet ef database update` 验证。

## 8. 上线后运维

- 监控指标（先用 `ILogger` + 后续接 OpenTelemetry）：`mcp.requests`、`mcp.tool.duration{tool=}`、`mcp.tool.errors{tool=,reason=}`、`mcp.token.lastUsed`。
- 安全：生产环境用反向代理（Nginx）在 `/mcp` 前置 IP 白名单，与 `/Auth/Login` 同等保护。
- 文档：交付物含 `Docs/三方对接文档/MCP管理服务对接指南.md`（阶段 5 写）。
- 应急：详见 §6.5 第 5 条。

## 9. 与既有规划的关系

- 与 [16-第一阶段项目架构方案.md](../../已完成功能规划/运营平台/16-第一阶段项目架构方案.md) 不冲突：本方案只在 Admin 内增模块，不动用户侧 API、不动桌面端、数据库仅新增 `ManagerMcpToken` / `ManagerAuditLog` / `IdempotencyRecord` 三张辅助表。
- 与 [25-执行计划-正式风控扫描与签名校验闭环.md](../../已完成功能规划/运营平台/25-执行计划-正式风控扫描与签名校验闭环.md) 联动：阶段 2 的"审核类工具"会和风控决策流耦合，到时再把 `RiskService` 注入工具类。

## 10. 风险与回滚

| 项目 | 风险 | 处置 / 回滚 |
| --- | --- | --- |
| MCP SDK 不稳定 | 0.x 接口可能变 | 锁版本 + 集成测试拦截；阶段 0 先做 SDK Spike 确认 API |
| AOT 误启用 | 工程发布失败 | `IsAotCompatible=false` + `<PublishTrimmed>false</PublishTrimmed>` + CI 校验 |
| Token 泄露 | 管理员误把 Token 提交到 Git | 后台一键吊销、TokenID 维度审计可定位调用 |
| 工具误删数据 | AI 走神 | MVP 不暴露删除/退款类工具；写工具全部进审计 + 幂等键 |
| 数据库迁移失败 | SqlServer/Sqlite 方言差异 | 双方言 `database update` 演练；有 Compiled Models 需重新生成 |
| 智能体重试导致重复扣款 | 网络抖动 | 写工具强约 `requestId` 幂等键（§3.5） |
| Manager 被禁用但令牌仍能用 | 鉴权链路漏校验 | `AdminMcpAuthHandler` 强制二次校验 `Manager.Status==0`（§3.2） |
| `settings_update` 改坏关键开关 | LLM 误操作 | MVP 不暴露；阶段 4 上线时强制白名单 + 审计 + 超级管理员 Power |
| 充值 / 资金不留痕 | Operations 历史缺陷 | 阶段 2 抽 Operations 时一并补 `Transaction` 流水（§4.7） |

## 11. 交付物清单

1. 本策划文档（`28`）。
2. 执行计划文档（`29-执行计划-MCP管理服务接入.md`）。
3. 代码改动（按执行计划分阶段提交）：
   - 实体 + 迁移：`ManagerMcpToken` / `ManagerAuditLog` / `IdempotencyRecord`。
   - 共享应用服务层：`Operations/*.cs`。
   - MCP 模块：`Mcp/*.cs` 含鉴权、上下文、工具、JSON 源生成、限流。
   - 管理员个人页：`ProfileController` + `Views/Profile/Mcp.cshtml`。
4. 测试：单元 + 集成（`WebApplicationFactory<Program>`）。
5. 三方对接文档：`Docs/三方对接文档/MCP管理服务对接指南.md`（阶段 5 交付）。

## 12. 复盘记录（v0.2）

> v0.1 → v0.2 的关键修正：

1. **数据模型改用独立表 `ManagerMcpToken`**：放弃复用 `ManagerProperty`，换来多令牌、轮换重叠、IP/UA 审计能力（§3.1）。
2. **AuthHandler 必须二次校验 `Manager.Status`**：补漏，避免禁用管理员账号而令牌仍可用的安全漏洞（§3.2）。
3. **写工具引入 `requestId` 幂等键**：避免 LLM 重试导致重复充值/重复批通过（§3.5）。
4. **审计前置到阶段 1 末**：MVP 上线即有审计落库（最小版只记元信息），不再等阶段 4（§4.8）。
5. **角色 `Power` 分级落地**：每个写工具显式声明所需 Power，MVP 简化为"仅超级管理员调写工具"（§3.4）。
6. **Tool 命名改 `snake_case`**：兼容性更好（§4.1）。
7. **列表精简版 + 详情完整版双层结构**：MCP 列表 `pageSize` 上限 20，减少 LLM 上下文消耗（§4.5）。
8. **高级筛选收进 `filter` 子对象**：避免工具 schema 暴涨（§4.5）。
9. **`settings_update` 移出 MVP**：阶段 4 才与白名单+审计同时上线（§4.6）。
10. **顺手修充值流水缺陷**：抽 `RechargeAsync` 时同时建 `Transaction` 行（§4.7）。
11. **错误码字典统一**：`NOT_FOUND/FORBIDDEN/VALIDATION/RATE_LIMITED/PROTECTED/CONFLICT/IDEMPOTENT_REPLAY/INTERNAL`（§4.9）。
12. **不暴露 Resources / Prompts**：本期只 Tools，安全与工程权衡（§4.10）。
13. **生产 `Mcp:Enabled` 默认 `false`**：必须运维显式开启（§6 / §6.5）。
14. **新增 `/mcp/health` 不鉴权探活端点**（§6.5）。
15. **AOT 防御加 `<PublishTrimmed>false</PublishTrimmed>` 显式声明**（§5.3）。
16. **集成测试落地 `WebApplicationFactory<Program>`**：SDK 升级回归不靠手测（§7.2）。
17. **数据库迁移步骤明确**：双方言 + Compiled Models 重生成（§7.4）。
18. **令牌一次性明文 UX 加强**：复制按钮、二次确认、`beforeunload` 提示（§3.6）。
19. **日志级别配置项独立**：`Logging:LogLevel:Netor.Cortana.Platform.Admin.Mcp`（§6）。
20. **阶段 0 加 SDK Spike**：0.5 天先跑通 hello-world + tools/list，验证实际 API 形态（详见 [29 执行计划](29-执行计划-MCP管理服务接入.md)）。
