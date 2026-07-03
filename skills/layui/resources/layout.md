# 框体布局

## 作用

提供中后台页面的大框体布局能力，适合实现左侧导航、顶部栏、内容区、页脚等整体页面结构。

## 典型结构

```html
<div class="layui-layout layui-layout-admin">
  <div class="layui-header">顶部栏</div>
  <div class="layui-side layui-bg-black">侧边栏</div>
  <div class="layui-body">内容区</div>
  <div class="layui-footer">页脚</div>
</div>
```

## 特点

- 适合管理系统页面主体结构
- 需要结合业务自行实现 iframe 跳转、侧边菜单收缩等行为
- 可参考社区 Admin UI 主题
- 更适合与 `nav`、`menu`、`tabs`、`table` 组合使用

## 常用类

| 类名 | 说明 |
| --- | --- |
| `layui-layout` | 布局容器 |
| `layui-layout-admin` | 管理后台布局模式 |
| `layui-header` | 顶部栏 |
| `layui-side` | 侧边栏 |
| `layui-body` | 主体内容 |
| `layui-footer` | 底部页脚 |

## AI 使用规则

- 中后台页面优先使用统一布局，不要把导航、内容和弹窗全部塞进一个页面片段。
- 布局类页面应先规划内容区，再决定是否使用 `tabs` 作为多页容器。
- 侧边栏折叠、顶部菜单切换、iframe 跳转都属于业务行为，需要额外编写 JS。
