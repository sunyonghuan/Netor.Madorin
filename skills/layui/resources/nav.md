# 导航菜单

## 作用

用于垂直导航、侧边菜单与面包屑导航，是管理后台页面结构的核心组件之一。

## API

- `element.render('nav', filter)`
- `element.on('nav(filter)', callback)`

## 常用属性

| 属性 | 说明 |
| --- | --- |
| `lay-accordion` | 手风琴效果 |
| `lay-bar` | 显示条形选中样式 |
| `lay-unselect` | 取消可选中状态 |
| `lay-filter` | 过滤器标识 |

## 常见结构

```html
<ul class="layui-nav layui-nav-tree" lay-filter="sideNav">
  <li class="layui-nav-item layui-this"><a href="javascript:;">首页</a></li>
  <li class="layui-nav-item"><a href="javascript:;">用户管理</a></li>
</ul>
```

## AI 使用规则

- 导航菜单应与路由或 iframe 页面切换逻辑配合。
- 如果要做高亮联动，必须统一管理 `lay-filter` 和当前路径。
- 侧边菜单建议与 `layout` 搭配，不要单独写成散乱的导航块。

## 文档地址

- `https://layui.dev/docs/2/`
