# 03. Razor + LayUI 技术路线

## Razor 编写原则

所有界面保持 Razor 模式：

```text
Views/**/*.cshtml
Views/Shared/**/*.cshtml
```

约定：

- 页面结构使用 Razor 输出。
- 表单字段使用 Razor 输出。
- 筛选区使用 Razor 输出。
- 表格容器和工具栏使用 Razor 输出。
- `lay-filter`、`lay-submit`、`lay-data`、`lay-event` 等 LayUI 属性直接写在 Razor 中。
- 表格行操作模板使用 Razor 页内 `<script type="text/html">` 模板。
- 不依赖 `Netor.Operates.LayuiHelpers` 生成控件。

## 组件映射

| 场景 | LayUI 组件 |
|---|---|
| 主列表 | `table.render()` |
| 筛选表单 | `form` |
| 日期范围 | `laydate` |
| 右侧抽屉 | `layer.open()` + 右侧定位 |
| 底部抽屉 | `layer.open()` + 底部定位 |
| 行内更多操作 | `dropdown` 或 table toolbar |
| 上传资源包 | `upload` |
| Tab/折叠/导航 | `element` |
| 提示和确认 | `layer.msg()`、`layer.confirm()` |

## 列表页示例

```html
<form class="layui-form layui-form-pane admin-filter-form" lay-filter="accountFilter">
  <div class="layui-inline">
    <label class="layui-form-label">关键词</label>
    <div class="layui-input-inline">
      <input type="text" name="keyword" class="layui-input" placeholder="账号/昵称/手机/邮箱">
    </div>
  </div>
  <button class="layui-btn" lay-submit lay-filter="accountSearch">查询</button>
  <button type="reset" class="layui-btn layui-btn-primary">重置</button>
</form>

<table id="accountTable" lay-filter="accountTable"></table>
```

## LayUI table 数据接口

新增数据接口不破坏现有 MVC 页面动作。每个列表页增加一个 table 数据接口。

返回格式：

```json
{
  "code": 0,
  "msg": "",
  "count": 128,
  "data": []
}
```

C# 结构建议：

```csharp
public sealed class LayuiTableResult<T>
{
    public int Code { get; init; } = 0;
    public string Msg { get; init; } = "";
    public int Count { get; init; }
    public IReadOnlyList<T> Data { get; init; } = [];
}
```

接口参数：

| 参数 | 说明 |
|---|---|
| `page` | 页码，LayUI 默认传入 |
| `limit` | 每页数量，LayUI 默认传入 |
| `field` | 排序字段 |
| `order` | 排序方向 |
| 业务筛选字段 | 当前 Razor 筛选表单提交 |

接口命名建议：

```text
GET /Accounts/TableData
GET /Accounts/TransactionsForAccount
GET /Accounts/OrdersForAccount
GET /Accounts/SubscriptionsForAccount
GET /Accounts/DownloadsForAccount

GET /Assets/TableData
GET /Assets/VersionsForAsset
GET /Assets/PricingPlansForAsset
GET /Assets/ReviewsForAsset

GET /Orders/TableData
GET /Orders/TransactionsTableData
```
