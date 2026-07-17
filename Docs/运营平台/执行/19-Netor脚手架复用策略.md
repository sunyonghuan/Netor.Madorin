# 19. Netor 脚手架复用策略

## 1. 目标

平台第一阶段不再自造基础架构，而是直接复用 Netor 体系中的账号、管理员、订单、交易、钱包和后台脚手架。

## 2. 复用顺序

优先级如下：

1. NuGet 包优先。
2. 只在包缺失或版本不适配时再用项目引用。
3. 不复制脚手架源码到平台项目。
4. 平台只保留业务差异代码。

## 3. 必须复用的基础包

- `Netor.Database.Abstractions`
- `Netor.Database.DbContextAbstractions`
- `Netor.Database.SqlLiteDbContextAbstractions`
- `Netor.Database.SourceGenerator`
- `Netor.Extensions.EnumExtensions`
- `Netor.Extensions.EnumSourceGenerator`
- `Netor.Extensions.EncryptExtensions`

后续 SQL Server 阶段再切换到：

- `Netor.Database.SqlServerDbContextAbstractions`

## 4. 数据库复用策略

`Netor.Cortana.Platform.Entitys` 已按以下模式设计：

- `Account : AccountBase`
- `Manager : AccountBase`
- `AccountRole / ManagerRole : RoleBase`
- `AccountRolePair : AccountRolePairBase<Account, AccountRole>`
- `AccountProperty / ManagerProperty : AccountPropertyBase<T>`
- `AccountWallet : WalletBase<Account>`
- `Order : OrderBase<Account>`
- `Transaction : TransactionBase<Account>`

这意味着后续后台页面和业务查询可以直接沿用 Netor 的用户、订单、交易、钱包语义，而不是再造一套平行模型。

## 5. `Netor.Operates` 复用策略

`Netor.Operates` 的价值在于后台结构和 Layui 页面组织方式。

平台后台 `Netor.Cortana.Platform.Admin` 应参考：

- MVC Controller + Razor View
- Layui 表格、弹层、表单
- Cookie Authentication
- 后台模块化 Controller 组织

第一阶段只做：

- 资产管理
- 分类管理
- 订单查看
- 订阅查看
- 用户查看

不需要一开始补齐完整权限、财务、通知等模块。

## 6. `Netor.Extensions` 复用策略

平台优先使用：

- `EncryptExtensions` 做密码哈希
- `EnumExtensions` 做枚举显示和转换
- `DateTimeExtensions` 做时间展示
- `StringExtensions` 做文本处理
- `DecimalExtensons` 做金额展示

当前平台项目中已移除未引用或未安装的 `JsonExtensions` 全局依赖，避免无意义编译错误。

## 7. 当前平台引用关系

### 7.1 `Netor.Cortana.Platform.Core`

保留基础模型、结果封装和选项类。

### 7.2 `Netor.Cortana.Platform.Entitys`

现在只负责：

- Netor 基类实体扩展
- EF Core DbContext
- 迁移和种子数据

### 7.3 `Netor.Cortana.Platform.Services`

只编排业务，不承担仓储职责。

### 7.4 `Netor.Cortana.Platform.Admin`

使用 MVC + Razor + Layui，后续页面尽量和 `Netor.Operates` 的字段命名、表格结构保持一致。

### 7.5 `Netor.Cortana.Platform.Api`

继续使用 Minimal API + Route Group，负责对外接口，不额外引入复杂层次。

## 8. 结论

本项目的复用重点不只是“引用几个包”，而是让数据库模型、后台页面和业务结构都靠近 `Netor.Operates`，这样后续页面复用、订单复用和交易复用才真正成立。

- `Microsoft.Extensions.Logging.RabbitMQ`

## 8. 架构方案调整建议

基于这四个脚手架项目，第一阶段平台架构应调整为：

```text
Netor.Cortana.Platform.Api
Netor.Cortana.Platform.Web
Netor.Cortana.Platform.Admin
Netor.Cortana.Platform.Services
Netor.Cortana.Platform.Entitys
Netor.Cortana.Platform.Core
```

其中：

- `Core` 只放平台自身缺失的基础约束，不重复 Netor.Extensions 已有能力。
- `Entitys` 复用 Netor.Database 的实体基类和 DbContext 基类。
- `Services` 复用 Netor.Extensions 的加密、JSON、字符串、金额扩展。
- `Admin` 参考 Netor.Operates.Admin 和 LayuiHelpers。
- `Api` 保持 Minimal API + Route Group，避免过重依赖。
- 日志先用 Console，后续按部署规模接 Netor.Logging。

## 9. 需要验证的问题

实施前需要确认：

- 这些 NuGet 包当前是否都在可用源中。
- 包版本是否支持 .NET 10 项目引用。
- `Netor.Database.SqlLiteDbContextAbstractions` 与 EF Core 版本是否和 .NET 10 兼容。
- `Netor.Operates.LayuiHelpers` 是否已发布到 NuGet。
- 是否需要统一升级脚手架包到 `net10.0` 或多目标框架。
- API AOT 发布时，这些包是否存在反射或 EF Core 兼容限制。

## 10. 总结

平台开发应建立在现有 Netor 脚手架生态上，而不是重新造基础轮子。

第一阶段重点复用：

- `Netor.Database`：实体基类、DbContext 基类、系统设置、账号订单基础结构。
- `Netor.Operates`：后台管理结构、Layui 管理界面经验、密码和认证配置。
- `Netor.Logging`：后续日志扩展能力。
- `Netor.Extensions`：加密、JSON、枚举、字符串、金额、时间等通用扩展。

平台只新增运营平台自身业务：资产、订阅、下载、市场展示、管理录入。
