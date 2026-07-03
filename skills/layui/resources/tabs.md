# 标签页 Tabs

## 作用

`tabs` 是 2.10+ 的新标签页方案，用于替代旧 `tab` 结构，适合多页签工作台、局部内容切换和管理后台导航。

## API

- `tabs.render(options)`
- `tabs.add(id, opts)`
- `tabs.close(id, index, force)`
- `tabs.closeMult(id, mode, index)`
- `tabs.change(id, index, force)`
- `tabs.data(id)`
- `tabs.getHeaderItem(id, index)`
- `tabs.getBodyItem(id, index)`

## 常见参数

| 参数 | 说明 |
| --- | --- |
| `elem` | 容器选择器 |
| `id` | 实例标识 |
| `data` | 标签数据源 |
| `active` | 默认激活项 |
| `allowClose` | 是否允许关闭 |
| `toolbar` | 工具条配置 |
| `beforeChange` | 切换前回调 |
| `afterChange` | 切换后回调 |
| `beforeClose` | 关闭前回调 |
| `afterClose` | 关闭后回调 |

## 标签数据建议

| 字段 | 说明 |
| --- | --- |
| `title` | 标签标题 |
| `content` | 内容区域 |
| `id` | 唯一标识 |
| `icon` | 可选图标 |
| `disabled` | 是否禁用 |

## 常见场景

- 多表单分步编辑
- 多列表筛选视图
- 工作台子页面切换
- 详情页与日志页并列展示

## 事件

- `afterRender`
- `beforeChange`
- `afterChange`
- `beforeClose`
- `afterClose`

## AI 使用规则

- 新 `tabs` 优先于旧 `tab`，除非用户明确要求兼容旧结构。
- 内容复杂时应使用数据驱动的 `render`，不要手工拼多个重复 DOM。
- 关闭操作应考虑是否允许强制关闭和未保存提示。

## 示例骨架

```html
<div id="workTabs"></div>
```

```js
layui.use(function(){
  var tabs = layui.tabs;
  tabs.render({
    elem: '#workTabs',
    id: 'workTabs',
    data: [{ title: '概览', content: '<div>内容</div>' }]
  });
});
```

## 文档地址

- `https://layui.dev/docs/2/`
