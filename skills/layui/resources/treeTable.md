# 树形表格

## 作用

基于 `table` 扩展的树形展示表格，适合组织结构、分类层级、权限菜单和目录数据。

## API

- `treeTable.render(options)`
- `treeTable.reload(id, options)`
- `treeTable.reloadData(id, options)`
- `treeTable.reloadAsyncNode(id, index)`
- `treeTable.getData(id, isSimpleData)`
- `treeTable.getNodeById(id, dataId)`
- `treeTable.getNodesByFilter(id, filter, opts)`

## 常见能力

- 父子层级展开折叠
- 异步加载子节点
- 按条件筛选节点
- 以表格列方式展示树节点属性

## 常见字段

| 字段 | 说明 |
| --- | --- |
| `id` | 节点唯一标识 |
| `pid` | 父节点标识 |
| `title` | 节点名称 |
| `children` | 子节点列表 |
| `spread` | 默认展开状态 |
| `disabled` | 是否禁用 |

## 使用建议

- 静态树适合一次性全量加载。
- 异步树适合超大数据和懒加载。
- 操作列中应使用 `lay-event` 配合树节点事件处理。

## AI 使用规则

- 如果数据天然具有层级关系，优先使用树形表格而不是自己拼接多级表格。
- 异步子节点加载要说明接口返回结构和节点标识字段。
- 树节点操作应与普通表格操作统一，避免单独写一套交互。

## 示例骨架

```html
<table id="deptTree" class="layui-hide"></table>
```

```js
layui.use(function(){
  var treeTable = layui.treeTable;
  treeTable.render({
    elem: '#deptTree',
    id: 'deptTree',
    data: []
  });
});
```

## 文档地址

- `https://layui.dev/docs/2/`
