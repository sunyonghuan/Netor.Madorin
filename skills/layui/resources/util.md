# 工具模块

## 作用

工具类方法与小组件集合，提供时间处理、字符串处理、窗口打开、事件监听与固定条能力。

## API

- `util.fixbar(options)`
- `util.countdown(options)`
- `util.timeAgo(time, onlyDate)`
- `util.toDateString(time, format, options)`
- `util.digit(num, length)`
- `util.escape(str)`
- `util.unescape(str)`
- `util.openWin(options)`
- `util.on(attr, events, options)`

## 常用能力

| 方法 | 说明 |
| --- | --- |
| `fixbar` | 固定条 |
| `countdown` | 倒计时 |
| `timeAgo` | 相对时间 |
| `toDateString` | 日期格式化 |
| `digit` | 数字补零 |
| `escape` | HTML 转义 |
| `unescape` | HTML 反转义 |
| `openWin` | 打开窗口 |
| `on` | 绑定属性事件 |

## AI 使用规则

- 工具模块适合封装通用小能力，不要替代业务组件。
- 时间、字符串、窗口类操作优先复用工具方法，保证统一性。

## 文档地址

- `https://layui.dev/docs/2/util/`
