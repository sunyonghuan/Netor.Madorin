# 栅格布局

## 作用

用于响应式页面分栏布局，适合表单排版、卡片布局、仪表盘和详情信息展示。

## 基本规则

- 使用 `layui-row` 定义行
- 使用 `layui-col-*-*` 定义列
- 一行总列数为 12
- 支持 `xs / sm / md / lg / xl`
- 支持列间距和列偏移

## 容器

- `layui-container`：固定宽度容器
- `layui-fluid`：流式 100% 宽度容器

## 常用类

| 类名 | 说明 |
| --- | --- |
| `layui-row` | 行容器 |
| `layui-col-md6` | 中等屏占 6 列 |
| `layui-col-sm12` | 小屏占满 |
| `layui-col-space15` | 行间距 |
| `layui-col-md-offset2` | 左偏移 2 列 |

## 示例骨架

```html
<div class="layui-fluid">
  <div class="layui-row layui-col-space15">
    <div class="layui-col-md6">左侧内容</div>
    <div class="layui-col-md6">右侧内容</div>
  </div>
</div>
```

## AI 使用规则

- 复杂页面先考虑栅格，再补组件，不要反过来堆 DOM。
- 移动端兼容时要显式写 `xs` 或 `sm` 断点。
