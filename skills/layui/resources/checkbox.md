# 复选框

## 作用

将原生 checkbox 增强为统一的 Layui 表单组件，支持默认风格、标签风格、开关风格以及自定义内容布局。

## 基础结构

```html
<input type="checkbox" name="agree" title="我已阅读并同意">
```

## 主要内容

- 默认风格
- 标签风格
- 开关风格
- 自定义标题模板 2.8.3+
- 自定义任意风格 2.9.8+
- 复选框事件

## 常用属性

| 属性 | 说明 |
| --- | --- |
| `name` | 字段名 |
| `title` | 显示标题 |
| `checked` | 默认选中 |
| `disabled` | 禁用 |
| `lay-skin` | 风格，如 `primary`、`switch` |
| `lay-text` | 开关时的显示文本 |

## 常见模式

### 普通复选框

```html
<input type="checkbox" name="remember" title="记住我">
```

### 开关风格

```html
<input type="checkbox" name="status" lay-skin="switch" lay-text="启用|禁用">
```

### 标签风格

```html
<input type="checkbox" name="tag" title="热门" lay-skin="primary">
```

## 事件

- 勾选状态变化事件
- 开关切换事件
- 多选联动事件

## AI 使用规则

- 如果是动态生成的复选框，渲染后必须调用 `form.render('checkbox')`。
- 开关类字段要同时考虑默认值与回显值。
- 批量选择场景建议与 `table.checkStatus()` 或列表数据联动。

## 示例骨架

```html
<form class="layui-form">
  <input type="checkbox" name="agree" title="我已阅读并同意" checked>
  <input type="checkbox" name="status" lay-skin="switch" lay-text="启用|禁用">
</form>
```

## 文档地址

- `https://layui.dev/docs/2/`
