# 滑块组件

## 作用

用于拖拽选值，常与表单配合，适合价格区间、音量控制、筛选条件和阈值配置。

## API

- `slider.render(options)`
- `inst.setValue(value, index)`

## 常用属性

| 属性 | 说明 |
| --- | --- |
| `elem` | 容器 |
| `type` | 类型 |
| `value` | 初始值 |
| `range` | 是否范围滑块 |
| `min` | 最小值 |
| `max` | 最大值 |
| `step` | 步长 |
| `showInput` | 是否显示输入框 |
| `tips` | 提示文本 |
| `setTips` | 提示自定义 |
| `change` | 变化回调 |
| `done` | 完成回调 |

## 常见场景

- 价格区间选择
- 分值阈值设置
- 音频音量调节

## AI 使用规则

- 若范围模式开启，需明确左右值含义。
- 数值范围和步长应与业务规则一致，避免前后端校验冲突。

## 文档地址

- `https://layui.dev/docs/2/slider/`
