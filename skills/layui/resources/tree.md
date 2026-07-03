# 树组件

## 作用

用于树状菜单、层级数据展示、权限选择和目录结构管理。

## API

- `tree.render(options)`
- `tree.getChecked(id)`
- `tree.setChecked(id, idArr)`
- `tree.reload(id, options)`

## 常用属性

| 属性 | 说明 |
| --- | --- |
| `elem` | 容器 |
| `data` | 数据源 |
| `id` | 实例 ID |
| `showCheckbox` | 是否显示复选框 |
| `edit` | 是否可编辑 |
| `accordion` | 是否手风琴 |
| `onlyIconControl` | 是否仅图标可操作 |

## 常见数据字段

| 字段 | 说明 |
| --- | --- |
| `title` | 节点标题 |
| `id` | 节点标识 |
| `children` | 子节点 |
| `spread` | 是否展开 |
| `checked` | 是否选中 |
| `disabled` | 是否禁用 |

## AI 使用规则

- 树组件适合权限、目录、分类，不要拿它替代普通列表。
- 如果需要复选联动，要明确父子联动规则。
- 需要编辑节点时，应说明是文本编辑、图标编辑还是拖拽排序。
