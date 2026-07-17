# 29 - 执行计划：后台 MCP 管理服务接入

> 配套文档：[28-MCP管理服务策划方案.md](28-MCP管理服务策划方案.md) （v0.2 复盘版）
>
> 范围：在 `Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin` 新增 MCP HTTP 服务，复用 `PlatformDbContext` 与既有 `Netor.Cortana.Platform.Services`，每个管理员账户在独立表 `ManagerMcpToken` 中自助管理多枚令牌；遵守"AOT 不启用"约束。
>
> v0.2 关键调整（详见 [策划方案 §12](28-MCP管理服务策划方案.md#12-复盘记录v02)）：独立令牌表 / `Manager.Status` 二次校验 / `requestId` 幂等 / 审计前置 / 角色 Power 分级 / `settings_update` 移出 MVP / 顺手修充值流水缺陷 / `Mcp:Enabled` 生产默认 `false` / 集成测试落地。

> 代码核对（2026-06-12）：当前仓库已实现 `AccountsTool` / `AssetsTool` / `DashboardTool`、MCP 鉴权、令牌页、最小审计、幂等记录、限流注册与账户/资源/仪表盘 Operations；但代码中尚不存在 `CategoriesTool`、`SubscriptionsTool`、`OrdersTool`、`SettingsTool`、`ReviewsTool`、`CreatorsTool`、`SettlementsTool`，也未落地完整审计 Payload、对接指南、双方言迁移演练和 MCP 自动化测试。因此本计划仍保留在未来版本策划，不归档到已完成。
>
> 追加核对（2026-06-23）：MCP 服务已经进入“可用 MVP / 试运行”阶段，基础通道和 10 个账户/资源/仪表盘工具可用，但帮助中心文章、分类和图片上传尚未开放给 MCP；本轮补齐 `DocsTool` 与跨站共享图片路由后，MCP 进度可从“基础可用”推进到“运营内容管理可用”。

---

## 时间汇总

| 阶段 | 工作量 | 关键输出 |
| --- | --- | --- |
| 阶段 0 立项 + SDK Spike | 1 天 | 版本锁定、SDK API 形态确认 |
| 阶段 1 鉴权 + 个人页 + 最小审计 | 2.5 天 | `ManagerMcpToken` / `ManagerAuditLog` 落库，可生成令牌 |
| 阶段 2 共享应用服务层 | 2 天 | `*Operations` 抽出，顺手修充值流水 |
| 阶段 3 MCP 端点 + MVP 工具 + 幂等 | 2.5 天 | `/mcp` 通、MVP 工具可调、`IdempotencyRecord` 落库 |
| 阶段 4 高敏工具 + 完整审计 | 2 天 | 阶段 2 工具集 + `settings_update` 白名单 + Payload 落库 |
| 阶段 5 文档发布验收 | 1 天 | 对接指南、生产开关、CI 校验 |
| **合计** | **11 天** | （含联调、双方言迁移、回归） |

> 当前实现进度（2026-06-06）：阶段 0~1 主体完成；阶段 2 已完成账户 Operations、资源 Operations、仪表盘 Operations 与充值流水修复；阶段 3 已接入 `/mcp`，完成账户 / 资源 / 仪表盘 10 个工具并通过生产配置烟测。分类、订阅、订单、设置只读工具、自动化单元/集成测试、双方言迁移演练、阶段 4/5 仍待推进。

> 追加执行说明（2026-06-23）：官网含帮助中心文章管理，文章包含 Markdown 正文和图片。官方网站程序与后台管理程序是两个独立站点，必须在两个站点的上一级建立共享媒体目录，并由 Admin / Web 同时映射同一个资源路由。默认资源路由固定为 `/docs-media/{categorySlug}/{articleSlug}/{fileName}`，MCP 图片上传返回该 URL 和 Markdown 片段；上传或保存文章时，Markdown 内部图片必须使用该路由。验收时必须确认三端都能正常显示图片：MCP 返回的 Markdown、后台编辑预览、官网文章页。
>
> 执行进度（2026-06-23）：已补 `DocsTool`、`DocOperations`、共享 `DocsMedia` 路径服务、Admin/Web `/docs-media` 静态路由、后台 Markdown 图片预览与 MVC 保存校验；Admin/Web 项目均已 `dotnet build --no-incremental` 通过。已在测试 SQLite 库启动 Admin/Web 完成真实图片上传后的三端显示验收：Admin `http://localhost:55107`、Web `http://localhost:55094`、测试库 `.tmp/platform-docs-runtime.db`、共享媒体目录 `.tmp/docs-media-runtime`。

---

## 阶段 0 — 立项 + SDK Spike（1 天）

### 0.1 立项确认（0.25 天）

- [x] 评审 [28-MCP管理服务策划方案.md](28-MCP管理服务策划方案.md) v0.2：架构、独立令牌表、幂等、审计前置、AOT 立场、工具清单。
- [x] 在 [Netor.Cortana.Platform.Admin.csproj](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Netor.Cortana.Platform.Admin.csproj) 补：

```xml
<PropertyGroup>
  <!-- MCP 模块依赖 EF Core 动态 LINQ + 反射建 Tool schema，不兼容 AOT/Trim；显式声明防误启用 -->
  <IsAotCompatible>false</IsAotCompatible>
  <PublishTrimmed>false</PublishTrimmed>
</PropertyGroup>
```

### 0.2 SDK Spike（0.5 天，关键）

> v0.1 直接照搬 `AddMcpServer().WithHttpTransport().WithToolsFromAssembly()` + `MapMcp()` 写法，但 `ModelContextProtocol.AspNetCore` 还在 0.x，方法名 / 扩展点很可能与设想不一致。等到阶段 3 才发现就晚了。

新建临时 throw-away 项目 `Spikes/McpHelloWorld/`：

- [x] 拉 NuGet 上当前最新稳定/预览版 `ModelContextProtocol` + `ModelContextProtocol.AspNetCore`，记下版本号。
- [x] 跑通 hello-world：注册一个空 Tool（`echo`），起 Kestrel，用 `mcp-inspector` 连成功调用。
- [x] 确认下列 API 的实际形态，写入下方"Spike 结论"：
  - 服务注册 API（是否真叫 `AddMcpServer().WithHttpTransport().WithToolsFromAssembly()`，还是变体名）。
  - `MapMcp("/mcp")` 是否存在；返回类型是否支持 `RequireAuthorization`/`RequireRateLimiting` 链式调用。
  - 工具拦截点（`IMcpServerToolFilter` 或同等接口）是否存在——审计阶段 1 要用。
  - JSON 选项注入路径（能否传 `JsonSerializerContext`）。
  - 工具入参 record 自动 schema 推导对 `[JsonRequired]` / `[Description]` / 嵌套 record 的支持度。

### 0.3 Spike 结论（回填）

| 问题 | 结论 |
| --- | --- |
| `ModelContextProtocol` 版本 | `1.4.0`（由 `ModelContextProtocol.AspNetCore` 传递引入） |
| `ModelContextProtocol.AspNetCore` 版本 | `1.4.0` |
| 服务注册 API | `builder.Services.AddMcpServer(...).WithHttpTransport().WithTools<TTool>()` 可编译通过；`WithToolsFromAssembly(...)` 仍存在，但 SDK XML 明确提示 NativeAOT 兼容优先使用泛型 `WithTools<T>()`。 |
| `MapMcp` 链式扩展 | `app.MapMcp("/mcp")` 存在，返回 EndpointConventionBuilder，可继续接 `RequireAuthorization(...)` / `RequireRateLimiting(...)`。 |
| Tool Filter 接口名 | 1.4.0 XML 中未发现计划里假设的 `IMcpServerToolFilter`；阶段 4 完整审计需改为工具包装/显式审计，或升级 SDK 后再评估统一拦截点。 |
| JSON Source-gen 注入路径 | `WithTools<TTool>(JsonSerializerOptions)`、`WithToolsFromAssembly(Assembly, JsonSerializerOptions)` 支持传入 JsonSerializerOptions，可通过 `TypeInfoResolverChain` 注入源生成上下文。 |

### 0.4 通过准则

- 相关人员对"管理员级 MCP、HTTP 模式、不启用 AOT"达成一致。
- Spike 结论已回填，后续阶段直接照实际 API 写。

---

## 阶段 1 — 鉴权 + 令牌管理 + 最小审计（2.5 天）

### 1.1 实体与迁移（0.5 天）

新增三个实体：

- [x] [Tables/Managers/ManagerMcpToken.cs](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Entitys/Tables/Managers/ManagerMcpToken.cs)：见 [策划方案 §3.1](28-MCP管理服务策划方案.md#31-数据模型采用独立表不复用-managerproperty) 字段表。
- [x] [Tables/Managers/ManagerAuditLog.cs](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Entitys/Tables/Managers/ManagerAuditLog.cs)：阶段 1 末"最小版"——`ID/ManagerID/TokenID?/ToolName/Source(MCP|MVC)/IP/UA/Success/ErrorCode?/DurationMs/CreatedUtc`，**不含 PayloadJson**（阶段 4 再补）。
- [x] 在 [PlatformDbContext](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Entitys/Data/PlatformDbContext.cs) `OnModelCreating` 注册：
  - `ManagerMcpToken`：`HasIndex(x => x.TokenHash).IsUnique()`、`HasIndex(x => x.ManagerId)`、外键 `Manager`。
  - `ManagerAuditLog`：`HasIndex(x => new { x.ManagerId, x.CreatedUtc })`、`HasIndex(x => x.ToolName)`。
- [x] 生成迁移：

```bash
dotnet ef migrations add Mcp_AddTokenAndAuditLog \
  --project Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Entitys \
  --startup-project Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Api
```

- [x] 重新生成 [CompiledModels/](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Entitys/Data/CompiledModels)（项目已启用源生成）。
- [ ] **双方言验证**：分别用 SqlServer 与 Sqlite 跑一次 `dotnet ef database update`，确保索引/外键在两种方言下都成立。
  - [x] 已在局域网 SQL Server 历史测试库验证运行时兼容补表路径。
  - [ ] SQLite `database update` 尚未执行。

### 1.2 令牌服务（0.5 天）

- [x] 新增 [Mcp/AdminMcpTokenService.cs](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Mcp/AdminMcpTokenService.cs)：
  - `Task<IReadOnlyList<TokenSummary>> ListByManagerAsync(string managerId, CancellationToken ct)`
  - `Task<TokenResolveResult?> ResolveAsync(string rawToken, CancellationToken ct)` —— 内部 `SHA256` → `WHERE TokenHash = <hash>` → 联表查 `Manager`，**校验 `Manager.Status == 0`**；命中返回 `(Token, Manager)` 元组。
  - `Task<string> CreateAsync(string managerId, string? note, CancellationToken ct)` —— 返回明文，仅这一次返回。
  - `Task RevokeAsync(string managerId, string tokenId, CancellationToken ct)` / `Task ToggleAsync(string managerId, string tokenId, bool enabled, CancellationToken ct)`。
  - `Task TouchLastUsedAsync(string tokenId, string? ip, string? ua, CancellationToken ct)` —— 异步、不阻塞鉴权响应。
  - 内部用 `RandomNumberGenerator.Fill(byte[32])` + `Base64Url`，前缀 `mcp_`。
  - 比较一律 `CryptographicOperations.FixedTimeEquals`。
- [x] 在 [Program.cs](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Program.cs) 注册 `AddScoped<AdminMcpTokenService>()`。

### 1.3 鉴权 / 授权（0.5 天）

- [x] 新增 [Mcp/AdminMcpAuthHandler.cs](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Mcp/AdminMcpAuthHandler.cs)：
  - 继承 `AuthenticationHandler<AuthenticationSchemeOptions>`。
  - 读 `Authorization: Bearer ...`，丢给 `AdminMcpTokenService.ResolveAsync`。
  - **命中且 `Token.Enabled=true` 且 `Manager.Status==0`** → 构造 `ClaimsIdentity`，附：
    - `ClaimTypes.NameIdentifier = ManagerID`
    - `ClaimTypes.Name = LoginUserName`
    - `ClaimTypes.Role = Role.Name`
    - `"ManagerNo" = No`
    - `"RolePower" = Role.Power`（用于阶段 3 角色判定）
    - `"TokenID" = Token.ID`（限流分区键 + 审计）
    - `"AuthMethod" = "AdminMcp"`
  - 异步 `TouchLastUsedAsync(tokenId, ip, ua)`，不阻塞响应。
  - 失败 → `AuthenticateResult.Fail("...")`。
- [x] [Program.cs](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Program.cs)：

```csharp
builder.Services.AddAuthentication() // 已有 Cookie
    .AddScheme<AuthenticationSchemeOptions, AdminMcpAuthHandler>("AdminMcp", _ => { });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build(); // MVC 仍走 Cookie
    options.AddPolicy("AdminMcp", p => p
        .AddAuthenticationSchemes("AdminMcp")
        .RequireAuthenticatedUser());
});
```

### 1.4 最小审计服务（0.25 天）

> 不等阶段 4——MVP 上线就要有元信息审计。

- [x] 新增 [Mcp/AdminAuditService.cs](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Mcp/AdminAuditService.cs)：
  - `Task LogAsync(AdminAuditEntry entry, CancellationToken ct)`：写 `ManagerAuditLog` 表（最小字段集，无 Payload）。
  - 内部 `SaveChangesAsync` 失败时只记 ILogger 不抛异常，避免审计故障污染主路径。
- [x] `AddScoped<AdminAuditService>()`。

### 1.5 管理员"我的 MCP 令牌"页（0.75 天）

- [x] 新增 [Models/Profile/McpTokenViewModel.cs](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Models/Profile/McpTokenViewModel.cs)：
  - `IReadOnlyList<TokenRow> Tokens`：`Id/Prefix/Note/Enabled/CreatedUtc/LastUsedUtc/LastUsedIp/LastUsedUa`
  - `string? PlainToken`：仅生成后 TempData 一次性
  - `string? PlainTokenNote`
- [x] 新增 [Controllers/ProfileController.cs](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Controllers/ProfileController.cs)：
  - `GET /Profile/Mcp` → 视图，列出当前管理员所有令牌。
  - `POST /Profile/Mcp/Generate` 入参 `note?` → `CreateAsync`，明文进 TempData，重定向 `Mcp`。
  - `POST /Profile/Mcp/Revoke` 入参 `id` → `RevokeAsync`。
  - `POST /Profile/Mcp/Toggle` 入参 `id, enabled` → `ToggleAsync`。
  - 全部 `[ValidateAntiForgeryToken]`。
- [x] 视图 [Views/Profile/Mcp.cshtml](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Views/Profile/Mcp.cshtml)（Layui）：
  - 多行令牌表格（前缀 / 备注 / 状态徽章 / 最近使用 / 操作）。
  - 顶部"生成新令牌"表单（备注 `note` 可选）。
  - 一次性明文展示卡片（**仅当 TempData 含明文时显示**）：
    - 大字号等宽字体展示。
    - "复制到剪贴板"按钮（`navigator.clipboard.writeText`）。
    - "我已妥善保存"复选框；未勾选时 `window.addEventListener('beforeunload', ...)` 阻止离开。
    - 红色强提示"页面刷新或离开后明文立即销毁，丢失只能撤销重新生成"。
- [x] 修改 [Views/Shared/_NavPartial.cshtml](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Views/Shared/_NavPartial.cshtml)：在最底部加"我的"分组（或 `_Layout` 顶部头像下拉），含"我的 MCP 令牌"链接。

### 1.6 验证

- [ ] 单元测试 `Tests/Netor.Cortana.Platform.Admin.Tests/Mcp/`：
  - `AdminMcpAuthHandlerTests`：
    - 无 `Authorization` 头 → `NoResult`。
    - 错误 Token → `Fail`。
    - 正确 Token + `Token.Enabled=false` → `Fail`。
    - 正确 Token + `Manager.Status != 0` → `Fail`（**v0.2 新增校验**）。
    - 正确 Token + 全部启用 → `Success`，`Identity.Claims` 含 `ManagerID/TokenID/RolePower`。
  - `AdminMcpTokenServiceTests`：
    - 生成 → `ListByManagerAsync` 看得到。
    - 多令牌共存（同一 Manager 创建 3 枚，逐枚禁用/撤销）。
    - `ResolveAsync` 哈希命中 vs 不命中。
- [x] 手测：浏览器登录后台 → `Profile/Mcp` 生成令牌 → MCP HTTP 调用。
  - [x] 已验证未带 Token 返回 401，不走 Cookie 登录跳转。
  - [x] 已验证生成、调用、吊销链路；吊销后同 Token 返回 401。

### 1.7 通过准则

- [ ] 单测全绿。
- [x] 后台能生成 / 列表 / 撤销 / 禁用启用令牌；明文只显示一次。
- [x] `AdminMcpAuthHandler` 已实现 `Manager.Status != 0` 二次校验；尚待自动化测试覆盖。
- [x] `ManagerAuditLog` 表已建好，阶段 3 MCP 写工具与 MVC 充值 / 重置密码路径已开始写入最小字段集。

---

## 阶段 2 — 共享应用服务层 + 顺手修充值流水（2 天）

> 目的：把 Controller 内嵌的查询/命令搬到 `*Operations`，让 MCP Tool 与 Controller 同一份业务规则；同时修一个历史缺陷——充值未写流水。

### 2.1 抽 Operations（1.5 天）

- [ ] 新增 `Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Operations/`：
  - [x] `OperationResult.cs`：`bool Success / string? ErrorCode / string? Message / T? Data`，错误码用 [策划方案 §4.9](28-MCP管理服务策划方案.md#49-错误码字典) 字典。
  - [x] `AccountOperations.cs`：`SearchAsync` / `GetAsync` / `SetStatusAsync` / `RechargeWalletAsync` / `ListTransactionsAsync` / `ResetPasswordAsync`。
  - [x] 首批命令切片：`UpdateProfileAsync` / `SetStatusAsync` / `BatchSetStatusAsync` / `RechargeWalletAsync` / `ResetPasswordAsync` / `GetWalletBalanceAsync`。
    - [x] 查询切片：`SearchAsync` / `GetAsync` / `ListTransactionsAsync`。
    - [x] 写状态校验：`SetStatusAsync` / `BatchSetStatusAsync` 限制状态只能为 `0/1/2`。
  - `AssetOperations.cs`、`CategoryOperations.cs`、`SubscriptionOperations.cs`、`OrderOperations.cs`、`SettingOperations.cs`、`DashboardOperations.cs`。
    - [x] `AssetOperations.cs`：资源搜索、详情、推荐开关、状态变更；发布路径复用审核与资源包风险校验，避免 MCP 绕过后台发布门槛。
    - [x] `DashboardOperations.cs`：后台首页级系统状态聚合，覆盖账户、资源、订单、钱包、订阅、运营待办、趋势、热门资源、运行时与 MCP 配置摘要；不暴露令牌、连接串、管理员明细或敏感配置。
    - [ ] `CategoryOperations.cs` / `SubscriptionOperations.cs` / `OrderOperations.cs` / `SettingOperations.cs` 尚未实现。
- [ ] 重构 [AccountsController](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Controllers/AccountsController.cs) 等：把对应逻辑改为调用 Operations，View / TempData / URL / 表单字段不变。
  - 渐进做法：先抽 `AccountOperations.SearchAsync` + `SetStatusAsync` 跑通样板；再批量迁移其他。
  - Controller 仍负责 HTTP / Drawer / TempData 这层胶水代码，不下沉到 Operations。
  - [x] 账户首批命令路径已改用 `AccountOperations`：基本信息、状态、批量状态、充值、重置密码。
- [x] 在 [Program.cs](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Program.cs) 注册：`AddScoped<AccountOperations>()`。
  - [x] 已注册 `AssetOperations` / `DashboardOperations`。
  - [ ] 其它 Operations 注册待对应服务实现后补齐。

### 2.2 顺手修充值流水缺失（0.5 天）

> [AccountsController.Recharge:715](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Controllers/AccountsController.cs#L715) 当前直接 `wallet.Money += amount` → `SaveChanges`，未写 `Transaction`。MCP 复用 Operations 后会放大这一缺陷。

- [x] `AccountOperations.RechargeWalletAsync(managerId, accountId, amount, source, requestId?, reason?, ct)`：
  - 在同一 `SaveChangesAsync` 内：
    - `wallet.Money += amount`
    - 新建 `Transaction` 行：`AccountID/No/OrderNo='MCP-RECHARGE'/Money/RealMoney/PayStatus=已支付/PayMethod=余额/PayTime=Now/Memo`，写入 `source`（`MCP|MVC`）、`requestId`、`managerId`（操作人）、`reason`。
  - 失败回滚整体（EF 默认行为）。
  - [x] 充值流水 `Type=9` 独立标记为"钱包充值"，避免和订单收入 `Type=1` 混淆。
  - [x] 在完整 `IdempotencyRecord` 服务落地前，先用 `Transaction.ThirdNo = {Source}-RECHARGE-{requestId}` 做基础去重；重复请求返回上次流水结果，不重复加钱。
  - [x] 新增 `IX_Transactions_ThirdNo` 索引，支撑充值去重查询。
- [x] 修 Controller：调用上述方法替代旧逻辑；表单加可选 `Reason` 字段（向后兼容，缺省 `"后台充值"`）。
  - [x] MVC 充值表单生成隐藏 `RequestId`，降低浏览器重复提交导致重复充值的风险。
  - [x] MVC 充值与重置密码写入 `ManagerAuditLog` 最小审计（Source=`MVC`，不含 Payload）。

### 2.3 验证

- [ ] `dotnet build Netor.Cortana.slnx` 全绿。
  - [x] 已执行 `dotnet build Src\Netor.Cortana.Platform\Netor.Cortana.Platform.Admin\Netor.Cortana.Platform.Admin.csproj --no-incremental`，0 警告 / 0 错误。
- [ ] 现有手测路径（账户列表、状态切换、充值、设置编辑）页面行为无可感知差异。
- [ ] 单元测试 `Tests/Netor.Cortana.Platform.Admin.Tests/Operations/`：
  - `AccountOperationsTests.RechargeAsync_WritesTransactionRow`：充值后查 `Transactions` 应有匹配行。
  - `AccountOperationsTests.RechargeAsync_ReplaysSameRequestId`：相同 `requestId` 不重复加钱，返回上次流水结果。
  - `AccountOperationsTests.SearchAsync_KeywordHitsLoginUserName` 等基础用例。
- [ ] 通过准则：Controller 不再直接 `dbContext.SaveChangesAsync`，事务边界由 Operations 持有；后台所有页面行为没有可感知差异；充值同时写流水。

---

## 阶段 3 — MCP 端点 + MVP 工具集 + 幂等（2.5 天）

### 3.1 接入 SDK（0.25 天）

> 严格按阶段 0 Spike 结论的实际 API 来写；下面伪代码以 v0.1 设想形态为准，回填后再调整。

- [x] [Netor.Cortana.Platform.Admin.csproj](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Netor.Cortana.Platform.Admin.csproj) 增加：

```xml
<PackageReference Include="ModelContextProtocol.AspNetCore" Version="1.4.0" />
```

`ModelContextProtocol` / `ModelContextProtocol.Core` 由 `ModelContextProtocol.AspNetCore` 传递引入，见附录 A。

- [x] [Program.cs](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Program.cs)：

```csharp
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "cortana-admin", Version = "1.0.0" };
    })
    .WithHttpTransport()
    .WithTools<AccountsTool>()
    .WithTools<AssetsTool>()
    .WithTools<DashboardTool>();
```

- [x] 在 `app.UseAuthentication()` 之后挂端点：

```csharp
app.MapMcp("/mcp")
    .RequireAuthorization("AdminMcp")
    .RequireRateLimiting("AdminMcpPerToken");

// /mcp/health 不鉴权探活，供 LB / K8s 用（策划方案 §6.5）
app.MapGet("/mcp/health", () => Results.Ok(new { status = "ok", version = "1.0.0" }))
    .AllowAnonymous();
```

- [x] `appsettings.json` / `appsettings.Development.json`：

```jsonc
"Mcp": {
  "Enabled": false,            // 生产默认关闭
  "RateLimit": { "PermitPerMinute": 60, "QueueLimit": 0 },
  "MaxRequestBytes": 1048576,
  "Logging": { "Verbose": false },
  "Idempotency": { "RetentionHours": 24 },
  "Settings": { "WriteWhitelist": [] }
}
```

`appsettings.json` 中 `Mcp:Enabled=false`，生产默认关闭；`appsettings.Development.json` 中 `Mcp:Enabled=true`。`Program.cs` 在 `builder.Configuration.GetValue("Mcp:Enabled", false)` 为 `false` 时不挂 `/mcp` 路由（仅保留 `/mcp/health`）。

### 3.2 共享上下文 + 角色判定（0.25 天）

- [x] 新增 [Mcp/AdminMcpContext.cs](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Mcp/AdminMcpContext.cs)：
  - 注入 `IHttpContextAccessor`。
  - `string ManagerId / long ManagerNo / string LoginUserName / string TokenId / long RolePower` 读自 Claims。
  - `string? RemoteIp / string? UserAgent` 读自 `HttpContext.Connection`/`Request.Headers`。
  - `OperationResult<T>? RequireSuperAdmin<T>()`：MVP 简化为"非超级管理员调写工具直接返回 `FORBIDDEN`"；后续按 [策划方案 §3.4](28-MCP管理服务策划方案.md#34-角色权限分级) 细化。
- [x] `AddHttpContextAccessor()` + `AddScoped<AdminMcpContext>()`。

### 3.3 幂等服务（0.5 天）

- [x] 新增实体 [Tables/Managers/IdempotencyRecord.cs](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Entitys/Tables/Managers/IdempotencyRecord.cs)：`ID/Hash/ResultJson/CreatedUtc`，`Hash` 唯一索引；`CreatedUtc` 索引（清理用）。
- [ ] 生成迁移 `Mcp_AddIdempotencyRecord`，重生成 CompiledModels，双方言 `database update`。
  - [x] 已生成迁移 `20260606132552_Mcp_AddIdempotencyRecord` 并重生成 CompiledModels。
  - [ ] 双方言 `database update` 待验证。
- [x] 新增 [Mcp/AdminMcpIdempotencyService.cs](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Mcp/AdminMcpIdempotencyService.cs)：
  - `Task<IdempotencyResult<T>?> TryReplayAsync<T>(string managerId, string toolName, string? requestId, ...)`：未传 `requestId` → 直接返回 null（不去重）；传了 → `Hash = SHA256(managerId + ":" + toolName + ":" + requestId)` 查表，命中反序列化 `ResultJson` 回放。
  - `Task RecordAsync<T>(string managerId, string toolName, string requestId, T result, CancellationToken ct)`：成功后落表。
  - 注意 `Hash` 唯一索引冲突（并发场景）→ 捕获 `DbUpdateException` 视为已记录，吞掉。
- [ ] 后台 `IHostedService` `IdempotencyCleanupService`：每小时清理 `CreatedUtc < UtcNow - RetentionHours` 的行（参考策划方案 §6 配置项）。

### 3.4 工具实现（1 天）

按 [策划方案 §4.2](28-MCP管理服务策划方案.md#42-阶段-1mvp仅只读--安全可逆轻量写) MVP 列表，每个工具一个文件。**命名一律 snake_case，写工具走幂等服务**。

- [x] `Mcp/Tools/DashboardTool.cs` —— `dashboard_summary`（只读）
- [x] `Mcp/Tools/AccountsTool.cs` —— `accounts_search` / `accounts_get` / `accounts_set_status` / `accounts_recharge_wallet` / `accounts_list_transactions`
  - [x] 首批写工具：`accounts_set_status` / `accounts_recharge_wallet`。
  - [x] 查询工具：`accounts_search` / `accounts_get` / `accounts_list_transactions`。
- [x] `Mcp/Tools/AssetsTool.cs` —— `assets_search` / `assets_get` / `assets_set_featured` / `assets_set_status`
- [x] `Mcp/Tools/DocsTool.cs` —— `docs_categories_list` / `docs_categories_upsert` / `docs_articles_search` / `docs_articles_get` / `docs_articles_upsert` / `docs_articles_set_status` / `docs_images_upload`
  - [x] `docs_images_upload` 写入两个站点上一级的共享目录，默认物理目录为 `<Admin|Web ContentRoot>/../Shared/docs-media`。
  - [x] `Mcp:MaxRequestBytes` 至少 8MB，覆盖 5MB 图片 Base64 编码后的 JSON-RPC 请求体。
  - [x] Admin 与 Web 同时映射同一个公开路由 `/docs-media`，不依赖各自 `wwwroot`。
  - [x] MCP 上传图片返回 `url=/docs-media/{categorySlug}/{articleSlug}/{fileName}` 与 `markdown=![alt](/docs-media/{categorySlug}/{articleSlug}/{fileName})`。
  - [x] `docs_articles_upsert` 校验 Markdown 内部图片引用：本地图片必须使用当前文章路由前缀 `/docs-media/{categorySlug}/{articleSlug}/`，外链图片仅允许 `http(s)`。
  - [x] 后台 Markdown 实时预览支持图片渲染，官网 `Markdig` 渲染保持同一路由可访问。
- [ ] `Mcp/Tools/CategoriesTool.cs` —— `categories_list` / `categories_create` / `categories_update`
- [ ] `Mcp/Tools/SubscriptionsTool.cs` —— `subscriptions_search` / `subscriptions_cancel`
- [ ] `Mcp/Tools/OrdersTool.cs` —— `orders_search` / `orders_get` / `orders_list_transactions`
- [ ] `Mcp/Tools/SettingsTool.cs` —— `settings_list` / `settings_get`（**`settings_update` 移到阶段 4**）

写工具模板：

```csharp
[McpServerToolType]
public sealed class AccountsTool(
    AccountOperations operations,
    AdminMcpContext ctx,
    AdminMcpIdempotencyService idempotency,
    AdminAuditService audit)
{
    [McpServerTool(Name = "accounts_recharge_wallet")]
    [Description("""
        给指定账户充值钱包余额。高敏写工具，仅超级管理员可用。
        副作用：wallet.Money += amount；同时写 Transaction 流水。
        幂等：建议传 requestId（UUID/ULID），重复调用回放上次结果。
        错误码：NOT_FOUND / FORBIDDEN / VALIDATION / IDEMPOTENT_REPLAY / INTERNAL。
        """)]
    public async Task<OperationResult<RechargeResult>> RechargeAsync(
        RechargeInput input,
        CancellationToken ct)
    {
        if (ctx.RequireSuperAdmin<RechargeResult>() is { } forbidden) return forbidden;

        var replay = await idempotency.TryReplayAsync<RechargeResult>(
            ctx.ManagerId, "accounts_recharge_wallet", input.RequestId, ct);
        if (replay is not null) return replay.AsOperationResult();

        var sw = Stopwatch.StartNew();
        var result = await operations.RechargeWalletAsync(
            ctx.ManagerId, input.AccountId, input.Amount,
            source: "MCP", requestId: input.RequestId, reason: input.Reason, ct);
        sw.Stop();

        await audit.LogAsync(new AdminAuditEntry(
            ManagerId: ctx.ManagerId,
            TokenId: ctx.TokenId,
            ToolName: "accounts_recharge_wallet",
            Source: "MCP",
            Ip: ctx.RemoteIp,
            Ua: ctx.UserAgent,
            Success: result.Success,
            ErrorCode: result.ErrorCode,
            DurationMs: (int)sw.ElapsedMilliseconds), ct);

        if (result.Success && input.RequestId is not null)
            await idempotency.RecordAsync(ctx.ManagerId, "accounts_recharge_wallet", input.RequestId, result.Data!, ct);

        return result;
    }
}
```

入参/出参规范（[策划方案 §4.5](28-MCP管理服务策划方案.md#45-工具元数据与响应规范)）：

- 列表精简版：`pageSize` 上限 20；返回精简字段。
- 详情完整版：`*_get`。
- 高级筛选收进可选 `filter` 子对象，主参数仅 5~6 个。
- 写工具入参带可选 `requestId`。
- 时间 ISO8601 UTC；金额 `decimal`。

### 3.5 JSON 源生成（0.25 天）

- [x] 新增 [Mcp/AdminMcpJsonContext.cs](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Mcp/AdminMcpJsonContext.cs)：

```csharp
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(OperationResult<RechargeResult>))]
[JsonSerializable(typeof(AccountSearchInput))]
[JsonSerializable(typeof(AccountSearchResult))]
// ... 每个 Tool 入参/出参 + OperationResult<> 闭包
internal sealed partial class AdminMcpJsonContext : JsonSerializerContext;
```

- [ ] 按阶段 0 Spike 结论注入 `TypeInfoResolverChain`。
  - [x] 当前 `AdminMcpJsonContext` 已用于幂等结果序列化 / 反序列化。
  - [ ] SDK Tool schema / HTTP JSON 的 resolver 注入尚未接入；DTO 源生成上下文已保留，后续可补。

### 3.6 限流（0.25 天）

- [x] [Program.cs](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Program.cs)：

```csharp
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("AdminMcpPerToken", httpContext =>
    {
        // 分区键用 TokenID（不是 ManagerID），避免一个管理员的两个智能体相互打架
        var tokenId = httpContext.User.FindFirst("TokenID")?.Value ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter(tokenId, _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue("Mcp:RateLimit:PermitPerMinute", 60),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = builder.Configuration.GetValue("Mcp:RateLimit:QueueLimit", 0)
            });
    });
});
app.UseRateLimiter();
```

### 3.7 验证

- [ ] 单元测试 `Tests/Netor.Cortana.Platform.Admin.Tests/Mcp/Tools/`：每个 Tool 1 个 happy path + 1 个失败路径（`NOT_FOUND` / `FORBIDDEN` / `VALIDATION` / `PROTECTED`）。
- [ ] 单元测试 `Mcp/IdempotencyTests`：相同 `requestId` 二次调用回放、不同 `requestId` 不回放、未传 `requestId` 不去重。
- [ ] **集成测试** `Tests/Netor.Cortana.Platform.Admin.IntegrationTests/`（v0.2 新增，必须）：
  - `Microsoft.AspNetCore.Mvc.Testing` + `WebApplicationFactory<Program>`，内存 SQLite 驱动。
  - 验 `POST /mcp` JSON-RPC：`initialize` → `tools/list` → 调用 `dashboard_summary` / `accounts_search` / `accounts_set_status`。
  - 未带 Token → 401；超频 → 429；非超级管理员调写工具 → `errorCode=FORBIDDEN`。
  - SDK 升级回归靠这套兜底。
- [x] 端到端：`dotnet run` / 生产配置本地启动 → MCP HTTP 客户端连 `http://localhost:<port>/mcp`，列工具 + 调用主流程。
  - [x] 已用 `ASPNETCORE_ENVIRONMENT=Production` + `Mcp__Enabled=true` 在局域网 SQL Server 测试库启动 Admin；`/mcp/health` 返回 200，未带 Token 的 `/mcp` 返回 401。
  - [x] 已通过后台登录 `admin` → `Profile/Mcp` 生成临时令牌 → MCP `initialize` → `notifications/initialized` → `tools/list` → 调用 `dashboard_summary` / `assets_search`；测试后临时令牌已吊销，吊销后再次调用返回 401。
  - [x] 已修复历史 `EnsureCreated` SQL Server 测试库缺少迁移历史导致 MCP 表未创建的问题：启动时识别旧库并补齐 `ManagerMcpTokens` / `ManagerAuditLogs` / `IdempotencyRecords` / `IX_Transactions_ThirdNo`。
  - [x] 已完整烟测 10 个 MVP Tool 注册与调用：`dashboard_summary`、`accounts_search`、`accounts_get`、`accounts_list_transactions`、`assets_search`、`assets_get` 读路径通过；写工具用虚假 ID / 非法状态覆盖 `VALIDATION` 与 `NOT_FOUND` 非破坏性分支；总计 22 项检查全部通过。
  - [x] 烟测发现历史种子数据中“超级管理员”角色 `Power=0`，导致 MCP 写工具统一返回 `FORBIDDEN`；已在新库种子和运行时初始化补偿中将超级管理员 `Power` 补齐为 100。测试后再次生成临时令牌复测通过，并已吊销临时令牌。
  - [ ] 尚未执行真实成功写入路径（如实际改账户状态、真实充值、真实上下架资源），需要在独立测试数据或回滚脚本准备好后再做。
- [x] 帮助中心图片三端验收：
  - [x] 通过 `docs_images_upload` 上传图片后，返回 URL 可通过 Admin 站点访问。
  - [x] 同一 URL 可通过 Web 站点访问。
  - [x] `docs_articles_upsert` 保存含 `![说明](/docs-media/{categorySlug}/{articleSlug}/{fileName})` 的 Markdown 后，后台预览和官网文章页均显示图片。
  - [x] 保存含相对图片路径或非 `/docs-media/{categorySlug}/{articleSlug}/` 本地图片路径的 Markdown 时返回 `VALIDATION`。
  - [x] 2026-06-23 运行态验收：`docs_images_upload` 返回 `/docs-media/getting-started/runtime-image-check/runtime-check.png` 与 Markdown 片段；Admin/Web 该图片路由均返回 `200 image/png`；官网文章 `/docs/getting-started/runtime-image-check` 返回 200，页面包含 `<img>` 与同一图片 URL；Playwright DOM 校验图片 `complete=true`、`naturalWidth=1`、`naturalHeight=1`；后台编辑页包含该 Markdown 图片与 `doc-markdown-preview` 预览容器。
- [ ] 手测：管理员浏览器 MVC 后台流程无回归（账户列表 / 状态切换 / 充值）。
  - [x] 已执行 `dotnet build Src\Netor.Cortana.Platform\Netor.Cortana.Platform.Admin\Netor.Cortana.Platform.Admin.csproj --no-incremental`，0 警告 / 0 错误；包含 `dashboard_summary` 编译验证。
  - [x] 已执行 `dotnet build Src\Netor.Cortana.Platform\Netor.Cortana.Platform.Admin\Netor.Cortana.Platform.Admin.csproj --no-incremental`，0 警告 / 0 错误；包含 `AssetsTool` / `AssetOperations` 编译验证。
  - [x] 已执行权限补偿后的 `dotnet build Src\Netor.Cortana.Platform\Netor.Cortana.Platform.Admin\Netor.Cortana.Platform.Admin.csproj --no-incremental`，0 警告 / 0 错误。

### 3.8 通过准则

- [x] 已实现的账户 / 资源 / 仪表盘 10 个工具可被 MCP HTTP 客户端调用；非破坏性读路径与失败路径烟测通过。
- [ ] 原计划 MVP 中的分类 / 订阅 / 订单 / 设置只读工具尚未实现，不能视为完整 MVP 工具集完成。
- [ ] 限流已接入 `AdminMcpPerToken`，但尚未执行超频 429 实测。
- [ ] 幂等服务已接入写工具成功路径，但尚未执行真实成功写入 + 重放 `requestId` 实测。
- [x] `ManagerAuditLog` 已开始填入（最小字段集），MCP 写工具与 MVC 充值 / 重置密码路径有审计调用。
- [x] `Mcp:Enabled=false` 默认关闭、`Mcp__Enabled=true` 生产路径显式启用已验证；`appsettings.Development.json` 为开发默认开启。

---

## 阶段 4 — 高敏工具 + 完整审计 + settings_update（2 天）

### 4.1 完整审计（0.5 天）

- [ ] 扩展 `ManagerAuditLog` 表：增加 `PayloadJson` / `ResultJson` / `Reason` 列；生成迁移 `Mcp_AuditFullPayload`。
- [ ] 实现 SDK Tool Filter（按阶段 0 Spike 结论的接口名，例如 `IMcpServerToolFilter`）：
  - 在 Tool 调用前后统一拦截，把入参 / 结果脱敏后写入 `PayloadJson` / `ResultJson`。
  - 脱敏字段：`Password` / `SafePassword` / `Token`（任何含 `token` 的字段）/ Settings 中 `IsProtection=true` 的 Value。
  - 写工具未传 `Reason` 时返回 `errorCode=VALIDATION`。
- [ ] Tool 上的手写审计调用改为依赖 Filter（避免双写）。
- [ ] `Mcp:Logging:Verbose=true` 时同时把脱敏后的 PayloadJson / ResultJson 写到结构化日志。

### 4.2 高敏工具集（1 天）

按 [策划方案 §4.3](28-MCP管理服务策划方案.md#43-阶段-2涉及风控--资金--配置的高敏写) 列表：

- [ ] `Mcp/Tools/ReviewsTool.cs` —— `reviews_list_pending` / `reviews_approve` / `reviews_reject`
- [ ] `Mcp/Tools/CreatorsTool.cs` —— `creators_search` / `creators_approve` / `creators_suspend`
- [ ] `Mcp/Tools/SettlementsTool.cs` —— `settlements_search` / `settlements_mark_paid` / `settlements_add_note`
- [ ] `AccountsTool.cs` 增 `accounts_reset_password`：`Reason` 必填、写审计、调用 `AccountOperations.ResetPasswordAsync`。
- [ ] 全部走幂等服务 + 角色 Power 分级（仅超级管理员）。

### 4.3 settings_update + 白名单（0.5 天）

- [ ] `Mcp:Settings:WriteWhitelist` 默认填运营级开关 Key 列表：`resource.featured.limit` / `download.daily.limit` / `notification.email.enabled` / `notification.webhook.url` / `system.maintenance.enabled` / `system.maintenance.message`。
- [ ] `SettingsTool.cs` 增 `settings_update`：
  - 入参 `key / value / reason / requestId?`。
  - 命中 `IsProtection=true` 或不在白名单 → `errorCode=PROTECTED`。
  - 其余调用 `SettingOperations.UpdateAsync`。
  - 全程审计；幂等。
  - 仅超级管理员。

### 4.4 验证

- [ ] 单元测试：审计 Filter 写入；脱敏字段不含敏感原文；白名单外 Key 拒绝。
- [ ] 集成测试：`reviews_approve` happy path；`accounts_reset_password` 后旧密码失效、新密码可登。
- [ ] 手测：用 `accounts_reset_password` 重置某测试账户，验证后台登录可用、审计 `PayloadJson` 中密码字段已脱敏。

---

## 阶段 5 — 文档发布验收（1 天）

### 5.1 对接文档

- [ ] 编写 `Docs/三方对接文档/MCP管理服务对接指南.md`：
  - 接入 URL、`Authorization: Bearer mcp_xxx` 格式、`Mcp-Session-Id` 行为。
  - 错误码字典 + 限流提示 + 幂等键（`requestId`）使用。
  - 角色 Power 与可调工具映射。
  - Claude Desktop / 自定义智能体的接入示例（curl + JSON-RPC samples）。
  - 安全提示：HTTPS only、Token 不可入 Git、定期轮换。
- [ ] 在 [_NavPartial.cshtml](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Views/Shared/_NavPartial.cshtml) 顶部加链接到对接指南。

### 5.2 发布脚本与 CI 校验

- [ ] [Build/Platfrom.ps1](Build/Platfrom.ps1) **不修改**。验证：
  - `pwsh ./Build/Platfrom.ps1 -Configuration Release -Runtime linux-x64`，输出 `Realases/Admin/` 启动可用。
  - 三个 `.csproj` `IsAotCompatible=false` / `PublishTrimmed=false` 注释依然在。
- [ ] 在 CI（如 GitHub Actions / 现有构建脚本）加一条静态校验：

```bash
if grep -rE '<PublishAot>\s*true' Src/Netor.Cortana.Platform/ ; then
  echo "::error::Platform 项目禁止启用 PublishAot（见 28 策划方案 §5）"
  exit 1
fi
```

### 5.3 生产开关

- [ ] 生产 `appsettings.Production.json`：
  - `Mcp:Enabled` 默认 `false`，由运维通过环境变量 `Mcp__Enabled=true` 显式启用。
  - `Mcp:RateLimit:PermitPerMinute` 按真实流量调整。
  - `Mcp:Settings:WriteWhitelist` 与运营协调。
- [ ] 反向代理（Nginx / ALB）配置 `/mcp` 路径 IP 白名单（智能体出口 IP 段），文档化在对接指南。
- [ ] `/mcp/health` 不鉴权，可被 LB / K8s readinessProbe 直接探活。

### 5.4 验收清单

- [ ] **鉴权**：
  - 每个管理员独立令牌；可生成 / 吊销 / 禁用 / 启用，明文一次性显示。
  - `Manager.Status != 0` 时令牌即便 `Enabled=true` 也被拒。
- [ ] **HTTP 模式**：
  - `POST /mcp` 工作，`/mcp/health` 不鉴权可达。
  - 生产 `Mcp:Enabled=false` 时 `/mcp` 直接 404；置 true 后正常。
- [ ] **工具**：MVP 列表 + 阶段 4 高敏列表全部可用，写入受审计、写工具支持 `requestId` 幂等。
- [ ] **限流**：超频 429。
- [ ] **回归**：MVC 后台所有页面行为无可感知差异；充值同时写流水（修缺陷）。
- [ ] **AOT 防御（[附录 B](#附录-b--aot-防御核对表)）**：未启用，`.csproj` 显式 `IsAotCompatible=false` + `PublishTrimmed=false`，CI 静态校验生效。
- [ ] **测试**：单元 + 集成测试全绿；双方言（SqlServer/Sqlite）迁移演练通过。
- [ ] **文档**：对接指南 + 28/29 文档 + 三个新增 `appsettings` 区段说明齐全。

---

## 附录 A — 锁定版本（阶段 0 SDK Spike 完成后回填）

| 包 | 版本 | 来源 | 备注 |
| --- | --- | --- | --- |
| `ModelContextProtocol` | `1.4.0` | `ModelContextProtocol.AspNetCore` 传递依赖 | 阶段 0 Spike 结论 |
| `ModelContextProtocol.AspNetCore` | `1.4.0` | NuGet | 已在 Admin 项目锁定 |

## 附录 B — AOT 防御核对表

- [x] [Netor.Cortana.Platform.Admin.csproj](Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Admin/Netor.Cortana.Platform.Admin.csproj) 含 `<IsAotCompatible>false</IsAotCompatible>` 与 `<PublishTrimmed>false</PublishTrimmed>`，附中文注释。
- [x] 三个平台 Web 项目都未引入 `<PublishAot>` / `<PublishTrimmed>true</PublishTrimmed>` / `<TrimMode>`。
- [x] [Build/Platfrom.ps1](Build/Platfrom.ps1) 未新增 `-p:PublishAot=true`。
- [ ] CI 校验脚本生效（5.2 节）。
- [x] MCP 路径上没有 `JsonSerializer.Serialize<object>` / `JsonElement.Deserialize<object>` / `Activator.CreateInstance` 调用。
- [x] 当前已实现的账户 / 资源 / 仪表盘 Tool DTO 与 `OperationResult<>` 闭包已进入 `AdminMcpJsonContext`。

## 附录 C — 与策划方案的章节映射

| 计划阶段 | 策划文档章节 |
| --- | --- |
| 阶段 0 立项 + Spike | §5 AOT 立场 / §10 风险（SDK 不稳定） |
| 阶段 1 鉴权 + 个人页 + 最小审计 | §3.1~§3.6 鉴权设计、§4.8 审计前置 |
| 阶段 2 共享应用服务层 | §2 总体架构、§4.7 充值流水修复 |
| 阶段 3 MCP + MVP 工具 + 幂等 | §3.5 幂等、§4.1~§4.5 工具规范、§6 配置项 |
| 阶段 4 高敏工具 + 完整审计 + settings_update | §4.3 高敏工具、§4.6 settings_update、§4.8 完整审计 |
| 阶段 5 文档发布验收 | §6.5 部署前提、§7 测试、§8 运维、§11 交付物 |
| 全程 | §5 AOT 风险、§10 风险与回滚、§12 复盘记录 |
