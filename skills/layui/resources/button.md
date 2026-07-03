# 按钮

## 作用

用于页面操作触发，支持主题、尺寸、图标、圆角、组合与链接按钮等样式。

## 基本写法

```html
<button class="layui-btn">主要按钮</button>
```

## 内容

- 按钮主题
- 按钮尺寸
- 按钮圆角
- 按钮图标
- 按钮混搭
- 按钮组合
- 按钮容器

## 常用类

| 类名 | 说明 |
| --- | --- |
| `layui-btn` | 基础按钮 |
| `layui-btn-primary` | 主按钮 |
| `layui-btn-normal` | 常规按钮 |
| `layui-btn-warm` | 警示按钮 |
| `layui-btn-danger` | 危险按钮 |
| `layui-btn-disabled` | 禁用样式 |
| `layui-btn-sm` | 小尺寸 |
| `layui-btn-lg` | 大尺寸 |
| `layui-btn-radius` | 圆角 |
| `layui-btn-fluid` | 宽度自适应 |

## 组合方式

- 图标按钮：`<i class="layui-icon">` + 文本
- 分组按钮：多个按钮组合在一起
- 链接按钮：使用 `a` 标签并加 `layui-btn`

## AI 使用规则

- 主操作用高优先级按钮，次要操作用普通按钮。
- 删除、禁用、提交等危险操作要使用明显区分的样式。
- 页面按钮过多时应先规划分组和主次关系。
