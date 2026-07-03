# 图标

## 作用

用于按钮、菜单、状态提示、装饰图标和空状态展示。

## 特点

- 基于 iconfont 字体图标
- 支持 `font-class` 与 `unicode`
- 可直接与按钮、导航、表单等组件混用

## 常见用法

```html
<i class="layui-icon layui-icon-ok"></i>
<i class="layui-icon">&#xe67b;</i>
```

## 注意

- 跨域资源需要正确设置 `Access-Control-Allow-Origin`
- 图标字体地址变更后要同步检查 CSS 引入路径

## AI 使用规则

- 状态图标应与文字一起使用，避免只靠颜色表达含义。
- 空状态页建议配合图标和简短说明，而不是单独放一个大图。
