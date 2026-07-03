# 表格组件 table

## 作用

数据表格展示、分页、排序、筛选、编辑、工具栏、行操作、重载。

## 模块加载

```js
layui.use(function(){
  var table = layui.table;
  var form = layui.form;
  var layer = layui.layer;
  var dropdown = layui.dropdown;
});
```

## 最小 HTML 结构

```html
<table class="layui-hide" id="userTable" lay-filter="userTable"></table>
```

说明：

- `id` 用于 `table.render({ elem: '#userTable' })` 绑定表格。
- `lay-filter` 用于事件监听，如 `table.on('tool(userTable)', callback)`。
- `layui-hide` 可避免初始化前显示原始表格。

## 核心 API

| API | 参数 | 返回 | 说明 |
| --- | --- | --- | --- |
| `table.render(options)` | `object` | 实例/对象 | 渲染表格，最核心方法 |
| `table.init(filter, options)` | `string, object` | - | 初始化静态表格，`filter` 对应 `lay-filter` |
| `table.reload(id, options, deep)` | `string, object, boolean` | - | 完整重载表格，可更新列、URL、where 等 |
| `table.reloadData(id, options, deep)` | `string, object, boolean` | - | 仅重载数据，适合搜索、分页刷新 |
| `table.renderData(id)` | `string` | - | 重新渲染数据视图 |
| `table.updateRow(id, opts)` | `string, object` | - | 更新指定行数据 |
| `table.checkStatus(id)` | `string` | `object` | 获取复选框选中行 |
| `table.setRowChecked(id, opts)` | `string, object` | - | 设置行选中状态 |
| `table.getData(id)` | `string` | `array` | 获取当前页缓存数据 |

## table.render(options) 常用参数

| 参数 | 类型 | 默认值 | 说明 |
| --- | ---: | --- | --- |
| `elem` | `string/DOM` | 必填 | 表格绑定元素，如 `'#userTable'` |
| `id` | `string` | 自动 | 表格实例 ID；建议显式设置，便于 reload |
| `url` | `string` | - | 数据接口地址 |
| `method` | `string` | `get` | 请求方式：`get` / `post` |
| `where` | `object` | `{}` | 接口额外参数，常用于搜索条件 |
| `headers` | `object` | - | 自定义请求头，如 token |
| `contentType` | `string` | - | 请求内容类型 |
| `data` | `array` | - | 直接传入静态数据，和 `url` 二选一 |
| `cols` | `array` | 必填 | 表头配置，二维数组 |
| `page` | `boolean/object` | `false` | 是否开启分页；对象可配置分页参数 |
| `limit` | `number` | `10` | 每页条数 |
| `limits` | `array` | `[10,20,30...]` | 每页条数下拉选项 |
| `height` | `number/string` | - | 表格高度，如 `full-200` 或 `500` |
| `cellMinWidth` | `number` | - | 单元格最小宽度 |
| `toolbar` | `string/boolean` | `false` | 头部工具栏模板选择器或默认工具栏 |
| `defaultToolbar` | `array` | 内置 | 默认工具栏按钮，如 `filter`, `exports`, `print` |
| `totalRow` | `boolean` | `false` | 是否开启合计行 |
| `skin` | `string` | - | 风格：`line` / `row` / `nob` |
| `even` | `boolean` | `false` | 是否开启隔行背景 |
| `size` | `string` | - | 尺寸：`lg` / `sm` |
| `parseData` | `function` | - | 将后端任意响应格式解析为 Layui 标准格式 |
| `request` | `object` | - | 自定义分页请求参数名 |
| `response` | `object` | - | 自定义响应字段名 |
| `done` | `function` | - | 渲染完成回调 |
| `error` | `function` | - | 请求失败回调 |

## cols 列配置常用参数

`cols` 是二维数组。第一层表示表头行，第二层表示列。

```js
cols: [[
  {type: 'checkbox', fixed: 'left'},
  {field: 'id', title: 'ID', width: 80, sort: true},
  {field: 'username', title: '用户名', minWidth: 160},
  {field: 'status', title: '状态', templet: '#statusTpl'},
  {field: 'createdAt', title: '创建时间', width: 180},
  {title: '操作', toolbar: '#rowTools', width: 180, fixed: 'right'}
]]
```

| 参数 | 类型 | 说明 |
| --- | ---: | --- |
| `field` | `string` | 数据字段名 |
| `title` | `string` | 表头标题 |
| `type` | `string` | 特殊列：`checkbox` / `radio` / `numbers` / `space` |
| `width` | `number/string` | 固定宽度 |
| `minWidth` | `number` | 最小宽度 |
| `fixed` | `string` | 固定列：`left` / `right` |
| `sort` | `boolean` | 是否允许排序 |
| `hide` | `boolean` | 是否隐藏列 |
| `align` | `string` | 对齐：`left` / `center` / `right` |
| `totalRow` | `boolean/string` | 是否参与合计行或显示合计文本 |
| `edit` | `string` | 单元格编辑类型，常用 `text` |
| `toolbar` | `string` | 行工具栏模板选择器 |
| `templet` | `string/function` | 自定义单元格模板 |
| `event` | `string` | 单元格事件名，配合 `tool` 事件 |

## 后端接口标准返回格式

Layui table 默认期望接口返回：

```json
{
  "code": 0,
  "msg": "",
  "count": 100,
  "data": []
}
```

字段说明：

- `code`：状态码，默认 `0` 表示成功。
- `msg`：消息。
- `count`：总条数，用于分页。
- `data`：当前页数据数组。

如果后端格式不同，使用 `parseData` 转换：

```js
parseData: function(res){
  return {
    code: res.success ? 0 : 1,
    msg: res.message,
    count: res.total,
    data: res.items
  };
}
```

## 分页请求参数

默认请求参数通常包含：

- `page`：当前页码
- `limit`：每页数量

自定义参数名：

```js
request: {
  pageName: 'pageIndex',
  limitName: 'pageSize'
}
```

## 常用渲染示例

```html
<form class="layui-form layui-row layui-col-space16" lay-filter="searchForm">
  <div class="layui-col-md3">
    <input type="text" name="keyword" placeholder="用户名/邮箱" class="layui-input">
  </div>
  <div class="layui-col-md3">
    <select name="status">
      <option value="">全部状态</option>
      <option value="enabled">启用</option>
      <option value="disabled">禁用</option>
    </select>
  </div>
  <div class="layui-col-md3">
    <button class="layui-btn" lay-submit lay-filter="searchSubmit">搜索</button>
    <button type="reset" class="layui-btn layui-btn-primary">重置</button>
  </div>
</form>

<table class="layui-hide" id="userTable" lay-filter="userTable"></table>

<script type="text/html" id="toolbarTpl">
  <div class="layui-btn-container">
    <button class="layui-btn layui-btn-sm" lay-event="add">新增</button>
    <button class="layui-btn layui-btn-sm layui-btn-danger" lay-event="batchDelete">批量删除</button>
  </div>
</script>

<script type="text/html" id="rowTools">
  <a class="layui-btn layui-btn-xs" lay-event="edit">编辑</a>
  <a class="layui-btn layui-btn-danger layui-btn-xs" lay-event="delete">删除</a>
</script>

<script>
layui.use(function(){
  var table = layui.table;
  var form = layui.form;
  var layer = layui.layer;

  table.render({
    elem: '#userTable',
    id: 'userTable',
    url: '/api/users',
    method: 'get',
    page: true,
    limit: 20,
    toolbar: '#toolbarTpl',
    cellMinWidth: 120,
    cols: [[
      {type: 'checkbox', fixed: 'left'},
      {field: 'id', title: 'ID', width: 80, sort: true},
      {field: 'username', title: '用户名', minWidth: 160},
      {field: 'email', title: '邮箱', minWidth: 200},
      {field: 'status', title: '状态', width: 100, templet: function(d){
        return d.status === 'enabled'
          ? '<span class="layui-badge layui-bg-green">启用</span>'
          : '<span class="layui-badge">禁用</span>';
      }},
      {field: 'createdAt', title: '创建时间', width: 180},
      {title: '操作', toolbar: '#rowTools', width: 160, fixed: 'right'}
    ]],
    parseData: function(res){
      return {
        code: res.code,
        msg: res.msg,
        count: res.count,
        data: res.data
      };
    }
  });

  form.on('submit(searchSubmit)', function(data){
    table.reloadData('userTable', {
      page: { curr: 1 },
      where: data.field
    });
    return false;
  });

  table.on('toolbar(userTable)', function(obj){
    if (obj.event === 'add') {
      layer.msg('打开新增弹窗');
    }
    if (obj.event === 'batchDelete') {
      var checked = table.checkStatus('userTable').data;
      if (!checked.length) return layer.msg('请选择数据');
      layer.confirm('确定删除选中的 ' + checked.length + ' 条数据？', function(index){
        layer.close(index);
      });
    }
  });

  table.on('tool(userTable)', function(obj){
    var row = obj.data;
    if (obj.event === 'edit') {
      layer.msg('编辑：' + row.username);
    }
    if (obj.event === 'delete') {
      layer.confirm('确定删除该用户？', function(index){
        obj.del();
        layer.close(index);
      });
    }
  });
});
</script>
```

## 文档地址

- `https://layui.dev/docs/2/table/`

## 事件

| 事件 | 写法 | 说明 |
| --- | --- | --- |
| 头部工具栏 | `table.on('toolbar(filter)', callback)` | 点击 `toolbar` 模板中带 `lay-event` 的按钮 |
| 行工具 | `table.on('tool(filter)', callback)` | 点击行内 `toolbar` 模板按钮 |
| 单元格编辑 | `table.on('edit(filter)', callback)` | 单元格编辑后触发 |
| 行单击 | `table.on('row(filter)', callback)` | 单击行触发 |
| 行双击 | `table.on('rowDouble(filter)', callback)` | 双击行触发 |
| 复选框 | `table.on('checkbox(filter)', callback)` | 复选框选择变化 |
| 单选框 | `table.on('radio(filter)', callback)` | 单选框选择变化 |
| 排序 | `table.on('sort(filter)', callback)` | 表头排序触发 |

### tool 事件回调对象

常用字段：

- `obj.data`：当前行数据。
- `obj.event`：触发元素的 `lay-event` 值。
- `obj.tr`：当前行 DOM/jQuery 对象。
- `obj.del()`：删除当前行 DOM 并更新缓存。
- `obj.update(fields)`：更新当前行数据和视图。

## 重载规则

搜索条件变化时优先用 `reloadData`：

```js
table.reloadData('userTable', {
  page: { curr: 1 },
  where: { keyword: 'test' }
});
```

列结构、URL、分页配置变化时用 `reload`：

```js
table.reload('userTable', {
  url: '/api/users/archive',
  cols: [[{field: 'id', title: 'ID'}]]
});
```

## AI 使用注意事项

- 生成表格时必须提供 `id` 和 `lay-filter`。
- 涉及搜索必须使用 `form.on('submit(...)')` 并 `return false` 阻止默认提交。
- 删除操作必须用 `layer.confirm` 二次确认。
- 如果使用接口数据，必须说明后端返回格式，或提供 `parseData`。
- 如果需要分页，必须设置 `page: true`，并确认 `count` 字段存在。
- 行操作按钮必须放在 `script type="text/html"` 模板里，并设置 `lay-event`。
- 批量操作必须先用 `table.checkStatus(id).data` 获取选中数据。
- 动态更新当前行时优先用 `obj.update()`，删除当前行用 `obj.del()`。
- 事件
- 静态表格初始化
- 行/单元格交互
- 右键菜单
