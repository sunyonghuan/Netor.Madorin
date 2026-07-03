# 进度条

## 作用

用于任务进度、加载状态、上传进度和流程推进状态展示。

## API

- `element.render('progress', filter)`
- `element.progress(filter, percent)`

## 常用属性

| 属性 | 说明 |
| --- | --- |
| `lay-percent` | 百分比值 |
| `lay-showpercent` | 是否显示百分比 |

## 常见场景

- 文件上传进度
- 任务执行状态
- 统计完成率

## AI 使用规则

- 进度条数值应始终限制在 0 到 100 之间。
- 如果需要动态更新，优先使用 API 而不是直接改 DOM。
