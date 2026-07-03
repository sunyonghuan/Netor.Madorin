# Code 预览组件

## 作用

用于代码块美化与代码/预览双栏展示，适合文档页、示例页和教学页面。

## API

- `layui.code(options)`
- `codeInst.reload(options)`
- `codeInst.reloadCode(options)`

## 常用属性

| 属性 | 说明 |
| --- | --- |
| `elem` | 容器 |
| `code` | 代码内容 |
| `preview` | 预览内容 |
| `layout` | 布局方式 |
| `style` | 容器样式 |
| `codeStyle` | 代码区样式 |
| `previewStyle` | 预览区样式 |
| `id` | 实例 ID |
| `className` | 附加 class |
| `text` | 标题或说明 |
| `header` | 头部配置 |
| `ln` | 行号 |
| `theme` | 主题 |
| `encode` | 是否转义 |
| `lang` | 语言 |
| `codeRender` | 代码渲染回调 |
| `done` | 完成回调 |
| `onCopy` | 复制回调 |

## AI 使用规则

- 代码示例和预览内容要分离，避免把演示逻辑混在代码字符串里。
- 如果要展示可执行示例，应明确哪些区域是预览，哪些区域是源码。

## 文档地址

- `https://layui.dev/docs/2/code/`
