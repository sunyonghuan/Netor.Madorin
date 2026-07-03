# 流加载

## 作用

用于信息流追加加载和图片懒加载，适合内容瀑布流、评论区和无限滚动列表。

## API

- `flow.load(options)`
- `flow.lazyimg(options)`

## 常用属性

| 属性 | 说明 |
| --- | --- |
| `elem` | 容器 |
| `scrollElem` | 滚动容器 |
| `isAuto` | 是否自动加载 |
| `moreText` | 加载更多提示 |
| `end` | 结束提示 |
| `isLazyimg` | 是否启用懒加载 |
| `mb` | 触底阈值 |
| `direction` | 加载方向 |
| `done` | 加载回调 |

## 常见场景

- 滚动加载更多内容
- 图片懒加载
- 新闻列表连续加载

## AI 使用规则

- 流加载适合连续内容，不适合精确分页筛选。
- 结束状态和空数据状态要分别说明。

## 文档地址

- `https://layui.dev/docs/2/flow/`
