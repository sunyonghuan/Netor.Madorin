# 选择框

## 作用

用于将原生 `<select>` 增强为可搜索、可分组、可创建、可独立使用的 Layui 选择组件。

## 基础结构

```html
<div class="layui-form-item">
  <label class="layui-form-label">城市</label>
  <div class="layui-input-block">
    <select name="city">
      <option value="">请选择</option>
      <option value="beijing">北京</option>
      <option value="shanghai">上海</option>
    </select>
  </div>
</div>
```

## 主要内容

- 普通选择框
- 分组选择框
- 搜索选择框
- 独立选择框 2.9.12+
- 选择框事件

## 常用属性

| 属性 | 说明 |
| --- | --- |
| `name` | 表单字段名 |
| `lay-search` | 开启搜索 |
| `lay-creatable` | 允许创建新值 |
| `lay-append-to` | 下拉层挂载位置，如 `body` |
| `lay-ignore` | 忽略 Layui 渲染 |
| `disabled` | 禁用 |
| `multiple` | 多选 |

## 常见场景

### 分组选择

```html
<select name="category">
  <option value="">请选择</option>
  <optgroup label="系统">
    <option value="user">用户管理</option>
  </optgroup>
  <optgroup label="业务">
    <option value="order">订单管理</option>
  </optgroup>
</select>
```

### 可搜索选择

```html
<select name="department" lay-search>
  <option value="">请选择</option>
  <option value="dev">研发部</option>
  <option value="ops">运维部</option>
</select>
```

## 事件

- 选择变化事件
- 搜索匹配事件
- 创建项提交事件

## AI 使用规则

- 动态插入 `option` 后必须执行 `form.render('select')`。
- 表单内使用时，`select` 必须保留 `name`，否则无法提交。
- 搜索下拉适用于选项较多的场景，不要把短列表也强行做成搜索。
- 如果选项来自异步接口，先写空选项，再填充数据并重新渲染。

## 示例骨架

```html
<form class="layui-form">
  <div class="layui-form-item">
    <label class="layui-form-label">状态</label>
    <div class="layui-input-block">
      <select name="status" lay-search>
        <option value="">请选择</option>
        <option value="1">启用</option>
        <option value="0">禁用</option>
      </select>
    </div>
  </div>
</form>
```

## 文档地址

- `https://layui.dev/docs/2/`
