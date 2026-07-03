# 下拉菜单

## 作用

用于动态下拉菜单、复杂菜单、工具菜单和右键菜单，适合按钮组、表格行操作和更多操作入口。

## API

- `dropdown.render(options)`
- `dropdown.reload(id, options)`
- `dropdown.reloadData(id, options)`
- `dropdown.close(id)`
- `dropdown.open(id)`

## 常用属性

| 属性 | 说明 |
| --- | --- |
| `elem` | 触发元素 |
| `data` | 菜单数据 |
| `id` | 实例 ID |
| `trigger` | 触发方式 |
| `closeOnClick` | 点击后是否关闭 |
| `show` | 显示方式 |
| `align` | 对齐方式 |
| `isAllowSpread` | 是否允许展开 |
| `isSpreadItem` | 展开项配置 |
| `accordion` | 是否手风琴模式 |
| `ready` | 初始化完成回调 |

## 菜单数据建议

| 字段 | 说明 |
| --- | --- |
| `title` | 显示标题 |
| `icon` | 图标 |
| `href` | 链接 |
| `target` | 打开方式 |
| `disabled` | 是否禁用 |
| `children` | 子菜单 |
| `type` | 类型标识 |

## 常见场景

- 更多操作下拉
- 表格工具栏菜单
- 头像菜单
- 右键上下文菜单

## AI 使用规则

- 菜单数据应保持树形结构清晰，避免手工硬编码过深层 DOM。
- 点击后是否关闭要按业务决定，不要一律关闭。
- 动态刷新菜单应使用 `reloadData` 或 `reload`，不要直接重建整页。

## 示例骨架

```html
<button id="moreBtn" class="layui-btn">更多</button>
```

```js
layui.use(function(){
  var dropdown = layui.dropdown;
  dropdown.render({
    elem: '#moreBtn',
    data: [{ title: '刷新', id: 'refresh' }, { title: '导出', id: 'export' }]
  });
});
```

## 文档地址

- `https://layui.dev/docs/2/`
