# 模板引擎

## 作用

Layui 内置模板引擎，适合动态 HTML 片段渲染、列表拼接、条件显示与局部页面输出。

## API

- `laytpl(template, options)`
- `templateInst.render(data, callback)`
- `templateInst.compile(template)`
- `laytpl.config(options)`
- `laytpl.extendVars(variables)`

## 重点

- 支持旧/新标签风格
- 支持子模板导入

## 常见语法

| 能力 | 说明 |
| --- | --- |
| 输出变量 | 插入数据字段 |
| 条件判断 | if/else |
| 循环遍历 | 列表渲染 |
| 子模板 | 局部复用 |

## AI 使用规则

- 模板适合纯渲染，不适合塞复杂业务逻辑。
- 数据结构要先定义清楚，再写模板表达式。

## 文档地址

- `https://layui.dev/docs/2/laytpl/`
