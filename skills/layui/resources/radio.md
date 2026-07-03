# 单选框

## 作用

提供统一的单选样式和事件能力，用于互斥选项、状态选择和配置项选择。

## 基础结构

```html
<input type="radio" name="gender" value="male" title="男">
<input type="radio" name="gender" value="female" title="女">
```

## 主要内容

- 普通单选框
- 自定义标题模板
- 自定义任意风格
- 单选框事件

## 常用属性

| 属性 | 说明 |
| --- | --- |
| `name` | 组名，决定互斥关系 |
| `value` | 提交值 |
| `title` | 显示文本 |
| `checked` | 默认选中 |
| `disabled` | 禁用 |
| `lay-skin` | 风格 |

## 常见模式

### 基础单选

```html
<input type="radio" name="status" value="1" title="启用" checked>
<input type="radio" name="status" value="0" title="禁用">
```

### 自定义风格

```html
<input type="radio" name="level" value="1" title="一级" lay-skin="primary">
```

## 事件

- 选中变化事件
- 单选联动事件

## AI 使用规则

- 同组单选必须共享同一个 `name`。
- 动态切换单选项后要重新渲染表单。
- 用于回显编辑表单时，应先设置值，再执行渲染。

## 示例骨架

```html
<form class="layui-form">
  <input type="radio" name="status" value="1" title="启用" checked>
  <input type="radio" name="status" value="0" title="禁用">
</form>
```

## 文档地址

- `https://layui.dev/docs/2/`
