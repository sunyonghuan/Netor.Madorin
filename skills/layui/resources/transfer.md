# 穿梭框

## 作用

用于左右列表数据穿梭、候选项与已选项收集，常用于权限分配、标签选择和人员分组。

## API

- `transfer.render(options)`
- `transfer.reload(id, options)`
- `transfer.getData(id)`

## 常用属性

| 属性 | 说明 |
| --- | --- |
| `elem` | 容器 |
| `data` | 数据源 |
| `title` | 左右标题 |
| `id` | 实例 ID |
| `width` | 宽度 |
| `height` | 高度 |
| `showSearch` | 是否显示搜索 |
| `onchange` | 变更回调 |
| `dblclick` | 双击穿梭 |
| `parseData` | 数据解析 |

## 常见场景

- 角色权限分配
- 标签批量选择
- 用户组成员管理

## AI 使用规则

- 左右列表应明确源数据与目标数据字段。
- 如果需要回显已选项，要提前准备已选数据源。
- 复杂权限分配要明确是否支持搜索和双击移动。

## 文档地址

- `https://layui.dev/docs/2/transfer/`
