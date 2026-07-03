# 输入框 / Textarea

## 作用

用于增强原生 `input[type=text]`、`textarea` 的外观和交互能力，支持前后缀、点缀图标、数字步进、密码显隐、清空按钮、动态状态等。

## 基础结构

```html
<div class="layui-input-wrap">
  <input type="text" name="username" placeholder="请输入用户名" class="layui-input">
</div>
```

```html
<div class="layui-input-wrap">
  <textarea name="desc" placeholder="请输入说明" class="layui-textarea"></textarea>
</div>
```

## 常见能力

- 普通输入框
- 输入框点缀
- 前置和后置
- 前缀和后缀
- 动态点缀
- 数字输入框
- 密码显隐
- 内容清除
- 自定义动态点缀
- 点缀事件

## 常用属性

| 属性 | 说明 |
| --- | --- |
| `name` | 表单字段名，提交时必须存在 |
| `placeholder` | 占位提示 |
| `value` | 初始值 |
| `readonly` | 只读 |
| `disabled` | 禁用 |
| `maxlength` | 最大长度 |
| `lay-affix` | 启用点缀能力 |
| `lay-precise` | 适用于数字精确控制场景 |
| `lay-password` | 密码显隐 |
| `lay-clear` | 清空按钮 |
| `lay-append-to` | 点缀容器挂载位置 |

## 点缀类型

| 类型 | 说明 |
| --- | --- |
| 前缀 | 输入框左侧附加图标或文本 |
| 后缀 | 输入框右侧附加图标或文本 |
| 前置 | 与输入框并列的独立块 |
| 后置 | 与输入框并列的独立块 |
| 动态点缀 | 根据状态动态显示按钮或图标 |

## 典型用法

### 带前后缀

```html
<div class="layui-input-wrap">
  <div class="layui-input-prefix">￥</div>
  <input type="text" class="layui-input" placeholder="请输入金额">
  <div class="layui-input-suffix">元</div>
</div>
```

### 密码显隐

```html
<div class="layui-input-wrap">
  <input type="password" name="password" class="layui-input" lay-password placeholder="请输入密码">
</div>
```

### 清空按钮

```html
<div class="layui-input-wrap">
  <input type="text" name="keyword" class="layui-input" lay-clear placeholder="请输入关键字">
</div>
```

## 事件

- 点缀点击事件
- 清空事件
- 密码显示状态切换事件
- 自定义点缀动作事件

## AI 使用规则

- 所有表单字段都要写 `name`，否则 `form.val()` 和提交数据无法正确获取。
- 需要动态点缀时，先写静态 HTML，再补交互逻辑。
- 需要数字输入时，优先使用官方数字类点缀方案，不要手写复杂的自增自减逻辑。
- 输入框与表单联动时，修改值后要配合 `form.render()` 或对应局部渲染。
- `textarea` 也应统一放入 `layui-form` 容器内，保持样式和校验一致。

## 示例骨架

```html
<form class="layui-form" lay-filter="demoForm">
  <div class="layui-form-item">
    <label class="layui-form-label">用户名</label>
    <div class="layui-input-block">
      <input type="text" name="username" class="layui-input" lay-clear>
    </div>
  </div>
</form>
```

## 文档地址

- `https://layui.dev/docs/2/`
