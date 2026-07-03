# 定时提醒插件 重构方案

**目标插件：** `Plugins/Src/Cortana.Plugins.Reminder`
**版本目标：** 2.0.0（破坏性升级，无向后兼容）
**讨论日期：** 2026-06-21
**作者：** sunmeang + Claude

---

## 1. 背景与核心问题

### 1.1 语义问题
现有插件触发提醒时，发送的消息是裸文本：
```
[定时提醒] 买菜：记得去买菜
```
此消息被宿主以 `ChatRole.User` 注入 AI。AI 收到的是一句"像用户说的话"，但实际是系统事件，导致：
- AI 不知道这是系统触发，倾向于"被动回复"模式
- AI 不知道该做什么（播报？等待？执行？）
- 没有明确的角色与行动指引

### 1.2 调度问题
现有调度是 demo 级：
- 仅支持 once/daily/weekly/monthly/custom 枚举式重复
- 无 cron 表达式支持
- 无 missed-fire 补偿（插件宕机期间错过的提醒）
- 无 snooze（推迟）能力
- 持久化无原子保障
- daily +24h 不考虑 DST、月末边界
- 不支持中国农历

---

## 2. 关键架构事实（重构基础）

### 2.1 宿主输出通道架构（已确认）
宿主有 **4 个并行输出通道**，AI 生成的 token 同时广播给所有：

| 通道 | 类型 | 受 ActiveClientId 影响 |
|------|------|----------------------|
| `UiChatOutputChannel` | 进程内直连 Avalonia UI | ❌ 否 |
| `TtsPluginOutputChannel` | TTS 语音 | ❌ 否 |
| `WebSocketChatOutputChannel` | WebSocket | ✅ 是 |
| `VoiceChatOutputChannel`（旧） | TTS | ❌ 否 |

**结论：** Reminder 通过 WebSocket 触发 AI 后，主 UI 和 TTS 走进程内通道，照常显示与播报，不受 `ActiveClientId` 影响。**纯文字方案完全可行。**

### 2.2 LLM 协议约束
- Claude API 不允许 messages 数组中段或末尾出现 system 消息
- OpenAI 中段 system 可以，末尾 system 行为不确定
- **结论：** 不能改协议为 system role，必须保持 user role，靠**消息内容文本**告诉 AI 这是系统事件。

### 2.3 设计原则
- **不修改宿主任何代码**
- **完全用文字控制 AI 行为**
- 仅改 Reminder 插件源码、依赖、数据结构

---

## 3. 决策清单

| 决策项 | 取值 | 说明 |
|--------|------|------|
| 向后兼容 | ❌ 不兼容 | schema 完全重定义，旧数据废弃 |
| 调度策略 | once / cron / lunar | 三选一并存 |
| 农历支持 | ✅ | `System.Globalization.ChineseLunisolarCalendar` |
| 农历闰月默认 | `Skip` | 闰月不重复触发 |
| Cron 库 | `NCronTab` | AOT 友好 |
| 持久化 | JSON + 原子写入 | write-temp + rename |
| 文件 schema 版本 | `SchemaVersion = 2` | 起点 |
| 模板存储 | `{plugin_data_dir}/templates/*.md` | 用户可改 |
| 模板引擎 | 纯 `{placeholder}` 替换 | 无 Handlebars/Razor |
| 默认时区 | `Asia/Shanghai` | 可逐项覆盖 |
| 默认 MissedFirePolicy | `FireOnce` | 开机后只补一次 |
| AI 触发文本 | 走 user role + 系统事件框架 | LLM 兼容 |

---

## 4. 新数据结构

### 4.1 `ReminderItem`

```csharp
public sealed class ReminderItem
{
    public int SchemaVersion { get; set; } = 2;

    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";

    /// <summary>触发时给 AI 的行为指令。空 = 使用模板默认值。</summary>
    public string AiPrompt { get; set; } = "";

    // ── 调度策略（三选一）──
    /// <summary>"once" | "cron" | "lunar"</summary>
    public string ScheduleType { get; set; } = "once";

    public DateTimeOffset? OnceAt { get; set; }            // ScheduleType=once

    public string? CronExpression { get; set; }            // ScheduleType=cron
    public string CronTimeZone { get; set; } = "Asia/Shanghai";

    public LunarSchedule? Lunar { get; set; }              // ScheduleType=lunar

    // ── 触发控制 ──
    public int MaxTriggerCount { get; set; } = 1;          // 0 = 无限
    public int TriggeredCount { get; set; }

    // ── 运行时状态 ──
    public DateTimeOffset? NextTriggerTime { get; set; }
    public DateTimeOffset? SnoozeUntil { get; set; }
    public bool IsEnabled { get; set; } = true;

    // ── 错过触发策略 ──
    /// <summary>"FireOnce" | "FireAll" | "Skip"</summary>
    public string MissedFirePolicy { get; set; } = "FireOnce";

    // ── 审计 ──
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastTriggeredAt { get; set; }
    public List<string> Tags { get; set; } = [];
}

public sealed class LunarSchedule
{
    public int Month { get; set; }       // 1-12
    public int Day { get; set; }         // 1-30
    public int Hour { get; set; }        // 0-23
    public int Minute { get; set; }      // 0-59

    /// <summary>"Skip" | "Both" | "RegularOnly"</summary>
    public string LeapMonthBehavior { get; set; } = "Skip";
}
```

### 4.2 持久化文件结构

```
{plugin_data_dir}/
  reminders.json              # 主数据文件，根对象 { "schemaVersion": 2, "items": [...] }
  reminders.json.tmp          # 写入临时文件（write-rename 模式）
  templates/
    single.md                 # 单条提醒触发模板
    batch.md                  # 批量提醒触发模板
    item.md                   # 批量内单项模板
```

---

## 5. 调度算法设计

### 5.1 抽象接口

```csharp
public interface IScheduleStrategy
{
    /// <summary>计算从给定时间起的下次触发时间，null 表示无后续触发。</summary>
    DateTimeOffset? CalculateNext(ReminderItem item, DateTimeOffset from);
}
```

三个实现：
- `OnceScheduleStrategy` - 单次触发
- `CronScheduleStrategy` - 基于 NCronTab
- `LunarScheduleStrategy` - 基于 ChineseLunisolarCalendar

### 5.2 农历计算关键点

```csharp
public DateTimeOffset? CalculateNext(ReminderItem item, DateTimeOffset from)
{
    var s = item.Lunar!;
    var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
    var cal = new ChineseLunisolarCalendar();
    var localFrom = TimeZoneInfo.ConvertTime(from, tz).LocalDateTime;
    var startYear = cal.GetYear(localFrom);

    for (int yr = startYear; yr <= startYear + 3; yr++)
    {
        // 1) 常规月尝试
        var dt = TryBuildLunarDate(cal, yr, s.Month, isLeap: false, s.Day, s.Hour, s.Minute);
        if (dt is { } d && d > localFrom)
            return new DateTimeOffset(d, tz.GetUtcOffset(d));

        // 2) Both 模式下，闰月也尝试
        if (s.LeapMonthBehavior == "Both")
        {
            var leapMonth = cal.GetLeapMonth(yr);
            if (leapMonth > 0)
            {
                var leapDt = TryBuildLunarDate(cal, yr, s.Month, isLeap: true, s.Day, s.Hour, s.Minute);
                if (leapDt is { } d2 && d2 > localFrom)
                    return new DateTimeOffset(d2, tz.GetUtcOffset(d2));
            }
        }
    }
    return null;
}

private static DateTime? TryBuildLunarDate(
    ChineseLunisolarCalendar cal, int year, int lunarMonth, bool isLeap,
    int day, int hour, int minute)
{
    try
    {
        // 闰月在该年的实际月序号
        var leapMonthOrdinal = cal.GetLeapMonth(year);
        int actualMonthOrdinal;

        if (leapMonthOrdinal == 0)
        {
            // 该年无闰月
            if (isLeap) return null;
            actualMonthOrdinal = lunarMonth;
        }
        else
        {
            // 该年有闰月
            // leapMonthOrdinal 表示哪个序号是闰月（如 9 表示第 9 个月是闰月，对应农历 8 月之后）
            var beforeLeapLunarMonth = leapMonthOrdinal - 1; // 闰月对应的农历月号
            if (isLeap)
            {
                if (lunarMonth != beforeLeapLunarMonth) return null;
                actualMonthOrdinal = leapMonthOrdinal;
            }
            else
            {
                actualMonthOrdinal = lunarMonth <= beforeLeapLunarMonth
                    ? lunarMonth
                    : lunarMonth + 1;
            }
        }

        var maxDay = cal.GetDaysInMonth(year, actualMonthOrdinal);
        var actualDay = Math.Min(day, maxDay);  // 30 → 29 自动 fallback

        return cal.ToDateTime(year, actualMonthOrdinal, actualDay, hour, minute, 0, 0);
    }
    catch (ArgumentOutOfRangeException)
    {
        return null;
    }
}
```

### 5.3 调度循环

保留现有的 next-fire 模式，但改进：

```
启动:
1. 加载所有 ReminderItem
2. 扫描所有 NextTriggerTime < now 的 missed fires
3. 按 MissedFirePolicy 处理:
   - FireOnce: 仅触发一次（最近的），其余跳过到下次
   - FireAll:  全部立即触发（按时间顺序）
   - Skip:     全部跳过，重新计算 NextTriggerTime
4. 进入主循环

主循环:
1. 计算 min(NextTriggerTime, SnoozeUntil) 作为下次唤醒时间
2. Task.Delay 等待（或挂起，若无提醒）
3. 唤醒后:
   a. 处理所有 due 项
   b. 渲染模板生成消息
   c. 通过 WebSocket 发送给宿主
   d. 推进 NextTriggerTime 或删除（取决于 MaxTriggerCount/TriggeredCount）
4. 回到 1
```

---

## 6. 模板系统

### 6.1 占位符规范

| 占位符 | 含义 | 示例值 |
|-------|------|--------|
| `{title}` | 提醒标题 | 买菜 |
| `{message}` | 提醒内容 | 记得去买菜 |
| `{trigger_time}` | 触发时间（友好格式） | 2026-06-21 09:00（周日） |
| `{ai_prompt}` | AI 行动指令 | 用语音朗读并询问是否需要帮助 |
| `{repeat_info}` | 重复信息 | 每天 09:00 / 每周一 / 农历八月十五 |
| `{tags}` | 标签 | 工作, 会议 |
| `{schedule_type}` | 调度类型 | once / cron / lunar |

批量模板额外支持：
| 占位符 | 含义 |
|-------|------|
| `{count}` | 提醒条数 |
| `{items}` | 用 item.md 渲染的所有项拼接 |

`item.md` 中可用 `{index}` 表示当前项序号（1-based）。

### 6.2 默认模板内容

**`templates/single.md`：**
```
【系统事件·定时提醒触发】
此消息由定时提醒系统自动发送，并非用户手动输入。
请以主动助理身份处理，立即通知用户并等待响应。

提醒标题：{title}
提醒内容：{message}
触发时间：{trigger_time}
{repeat_info}

行动指令：{ai_prompt}
```

**`templates/batch.md`：**
```
【系统事件·批量定时提醒触发】
此消息由定时提醒系统自动发送，共 {count} 条提醒到期。
请以主动助理身份依次通知用户。

{items}
```

**`templates/item.md`：**
```
{index}. 【{title}】{message}
   行动：{ai_prompt}
```

### 6.3 模板加载与缓存

- 启动时检查 `templates/` 目录，缺失文件从程序集嵌入资源拷贝
- 内存缓存渲染器，文件 mtime 变化时自动重新加载（`FileSystemWatcher`）
- 渲染失败（占位符未关闭等）时 fallback 到嵌入资源默认模板

---

## 7. 工具层设计

### 7.1 主要工具列表

| 工具名 | 用途 |
|--------|------|
| `get_current_time` | 获取系统当前时间（保留） |
| `create_reminder` | 万能创建工具，支持三种调度类型 |
| `create_daily_reminder` | 快捷工具：每天定时提醒 |
| `create_weekly_reminder` | 快捷工具：每周特定日提醒 |
| `create_lunar_reminder` | 快捷工具：农历提醒 |
| `list_reminders` | 列出提醒（支持标签过滤） |
| `update_reminder` | 更新提醒属性 |
| `delete_reminder` | 删除单条 |
| `delete_reminders_by_tag` | 按标签批量删除 |
| `search_reminders` | 关键词搜索 |
| `snooze_reminder` | 推迟提醒（新增） |
| `enable_reminder` / `disable_reminder` | 启停 |

### 7.2 `create_reminder` 完整签名

```csharp
[Tool(Name = "create_reminder", Description = "...")]
public string CreateReminder(
    [Parameter(Description = "标题")] string title,
    [Parameter(Description = "提醒内容")] string message,
    [Parameter(Description = "调度类型：once / cron / lunar")] string scheduleType,
    [Parameter(Description = "scheduleType=once 时填，ISO 8601 格式")] string onceAt,
    [Parameter(Description = "scheduleType=cron 时填，5 字段表达式如 '0 9 * * 1-5'")] string cronExpression,
    [Parameter(Description = "scheduleType=lunar 时填，格式 'M-D HH:mm'，如 '8-15 20:00'")] string lunarSpec,
    [Parameter(Description = "时区 IANA 标识，默认 Asia/Shanghai")] string timeZone,
    [Parameter(Description = "AI 触发后行为指令，空使用模板默认")] string aiPrompt,
    [Parameter(Description = "最大触发次数，0=无限")] int maxTriggerCount,
    [Parameter(Description = "错过触发策略：FireOnce/FireAll/Skip")] string missedFirePolicy,
    [Parameter(Description = "标签，逗号分隔")] string tags);
```

### 7.3 `snooze_reminder`

```csharp
[Tool(Name = "snooze_reminder", Description = "推迟提醒触发时间。")]
public string SnoozeReminder(
    [Parameter(Description = "提醒 ID")] string id,
    [Parameter(Description = "推迟分钟数")] int minutes);
```

实现：设置 `SnoozeUntil = DateTimeOffset.Now + minutes`，调度器优先比较 `max(NextTriggerTime, SnoozeUntil)`。

---

## 8. 文件改动清单

| 文件 | 操作 | 说明 |
|------|------|------|
| `Cortana.Plugins.Reminder.csproj` | 修改 | 添加 `NCronTab` 包；版本号 2.0.0 |
| `ReminderItem.cs` | 重写 | 全新 schema |
| `LunarSchedule.cs` | 新建 | 农历调度参数 |
| `ScheduleStrategy/IScheduleStrategy.cs` | 新建 | 调度策略抽象 |
| `ScheduleStrategy/OnceScheduleStrategy.cs` | 新建 | 单次 |
| `ScheduleStrategy/CronScheduleStrategy.cs` | 新建 | NCronTab 包装 |
| `ScheduleStrategy/LunarScheduleStrategy.cs` | 新建 | 农历计算 |
| `ScheduleStrategy/ScheduleStrategyFactory.cs` | 新建 | 按 ScheduleType 派发 |
| `ReminderStore.cs` | 重写 | 原子持久化、新 schema、查询接口 |
| `ReminderScheduler.cs` | 重写 | 启动补偿、snooze 支持、模板渲染 |
| `Templates/TemplateRenderer.cs` | 新建 | 占位符替换 |
| `Templates/Embedded/single.md` | 新建嵌入资源 | 默认模板 |
| `Templates/Embedded/batch.md` | 新建嵌入资源 | 默认模板 |
| `Templates/Embedded/item.md` | 新建嵌入资源 | 默认模板 |
| `ReminderTools.cs` | 重写 | 新工具集 |
| `CortanaWsClient.cs` | 微调 | 不变核心逻辑（沿用 type=send） |
| `Startup.cs` | 修改 | 更新 Instructions、注册新服务、版本号 |
| `PluginJsonContext.cs` | 修改 | 注册新类型 |
| `HourlyFileLoggerProvider.cs` | 不变 | - |

---

## 9. AOT 兼容性核对

| 依赖 | AOT 状态 | 说明 |
|------|---------|------|
| `NCronTab` | ✅ 兼容 | 纯字符串解析，无反射 |
| `System.Globalization.ChineseLunisolarCalendar` | ✅ 兼容 | BCL 类型 |
| `System.Text.Json` 源生成器 | ✅ 兼容 | 已用 `PluginJsonContext` |
| `FileSystemWatcher` | ✅ 兼容 | BCL |
| `TimeZoneInfo.FindSystemTimeZoneById` | ⚠️ 需验证 | Windows 上需要 ICU 数据，已知 .NET 10 默认包含 |

PluginJsonContext 需新增注册：
```csharp
[JsonSerializable(typeof(ReminderItem))]
[JsonSerializable(typeof(List<ReminderItem>))]
[JsonSerializable(typeof(LunarSchedule))]
[JsonSerializable(typeof(ReminderFileRoot))]   // 新建：{ schemaVersion, items[] }
[JsonSerializable(typeof(WsClientMessage))]
[JsonSerializable(typeof(WsServerMessage))]
```

---

## 10. 测试要点

| 测试项 | 验证内容 |
|--------|---------|
| 一次性提醒 | once 类型，触发后自动删除 |
| Cron 工作日提醒 | `0 9 * * 1-5` 跨周末正确跳过 |
| 农历春节提醒 | `1-1 09:00` 正确算出公历 |
| 农历闰月（Skip） | 闰八月年份不重复触发八月十五 |
| 农历闰月（Both） | 闰八月年份触发两次 |
| 农历 30 日 fallback | 农历某月仅 29 天时降级到 29 日 |
| 启动补偿（FireOnce） | 关机一晚 5 条到期，重启仅触发最近 1 条 |
| 启动补偿（FireAll） | 5 条全部依次触发 |
| Snooze | 延迟 10 分钟后触发，期间不被原 NextTriggerTime 唤醒 |
| 模板编辑 | 修改 `templates/single.md` 后下次触发使用新模板 |
| 模板缺失 | 删除 `templates/` 目录，重启后自动生成 |
| 持久化原子性 | 写入过程模拟崩溃，重启后数据完整 |
| AI 语义 | 触发后 AI 主动通知，非"被动回复" |
| DST 转换 | Asia/Shanghai 无 DST，跳过；UTC 时区下验证 cron 正确 |

---

## 11. 实施任务分解

| # | 任务 | 估时 |
|---|------|------|
| 1 | 添加 NCronTab 依赖、骨架文件、删除旧代码 | 30 min |
| 2 | 实现 ReminderItem + LunarSchedule + ReminderFileRoot 新 schema | 30 min |
| 3 | 实现三个 IScheduleStrategy + 工厂 | 2 h |
| 4 | 实现 ReminderStore（原子写入、查询接口） | 1.5 h |
| 5 | 实现 TemplateRenderer + 嵌入资源 + 文件监听 | 1.5 h |
| 6 | 重写 ReminderScheduler（启动补偿、snooze、模板渲染） | 2 h |
| 7 | 重写 ReminderTools（11 个工具） | 2 h |
| 8 | 更新 Startup.cs Instructions + DI 注册 | 30 min |
| 9 | 更新 PluginJsonContext | 15 min |
| 10 | 本地编译 + AOT 发布验证 | 1 h |
| 11 | 手工测试（覆盖 §10 测试要点） | 2 h |
| **合计** | | **约 1.5 个工作日** |

---

## 12. 风险与缓解

| 风险 | 缓解 |
|------|------|
| NCronTab 在 AOT 下不工作 | 已确认无反射，已被多个 AOT 项目使用；首次构建时验证 |
| 农历闰月逻辑错误 | 单元测试覆盖闰八月/闰四月等历史样本 |
| TimeZoneInfo 在 AOT 找不到时区 | .NET 10 默认 ICU；如失败 fallback 到 UTC+8 固定偏移 |
| 模板字符串注入风险 | 占位符仅替换，不执行表达式；用户输入的 title/message 不解析为占位符 |
| AI 不按 `aiPrompt` 行事 | 模板可调，用户可在 `templates/single.md` 加更强约束语 |

---

## 13. 待确认事项（无）

讨论阶段已全部确认，可进入实施。
