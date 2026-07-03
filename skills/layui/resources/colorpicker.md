# 颜色选择器

## 作用

支持 hex / rgb / rgba 快速选色，适合主题配置、表单颜色字段和视觉设计工具。

## API

- `colorpicker.render(options)`

## 常用属性

| 属性 | 说明 |
| --- | --- |
| `elem` | 绑定元素 |
| `color` | 初始颜色 |
| `format` | 格式 |
| `alpha` | 是否支持透明度 |
| `predefine` | 预设颜色 |
| `colors` | 自定义颜色集 |
| `size` | 尺寸 |
| `change` | 变化回调 |
| `done` | 确认回调 |
| `cancel` | 取消回调 |
| `close` | 关闭回调 |

## AI 使用规则

- 如果颜色值要保存到后端，应统一格式，不要同页混用多种格式。
- 颜色选择器适合配置项，不适合频繁大批量表单项。

## 文档地址

- `https://layui.dev/docs/2/colorpicker/`
