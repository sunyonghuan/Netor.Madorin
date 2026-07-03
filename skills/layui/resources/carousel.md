# 轮播组件

## 作用

用于轮播、焦点图、内容切换、fullpage 类场景。

## API

- `carousel.render(options)`
- `inst.reload(options)`
- `inst.goto(index)`

## 常用属性

| 属性 | 说明 |
| --- | --- |
| `elem` | 容器 |
| `width` | 宽度 |
| `height` | 高度 |
| `full` | 是否全屏 |
| `anim` | 切换动画 |
| `autoplay` | 是否自动播放 |
| `interval` | 间隔时间 |
| `index` | 初始索引 |
| `arrow` | 左右箭头 |
| `indicator` | 指示器 |

## 常见场景

- 首页焦点图
- Banner 广告位
- 步骤式内容切换
- 全屏切页展示

## AI 使用规则

- 轮播中的每个子项应明确内容类型，避免空白轮播。
- 自动播放要考虑用户干预和移动端体验。

## 文档地址

- `https://layui.dev/docs/2/carousel/`
