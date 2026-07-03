# 表单组件 form

## 作用

表单渲染、验证、赋值取值、提交、事件监听、全局配置。

## 基础 HTML 结构

```html
<form class="layui-form" lay-filter="userForm">
  <div class="layui-form-item">
    <label class="layui-form-label">用户名</label>
    <div class="layui-input-block">
      <input type="text" name="username" lay-verify="required" placeholder="请输入用户名" class="layui-input">
    </div>
  </div>
  <div class="layui-form-item">
    <button class="layui-btn" lay-submit lay-filter="userSubmit">提交</button>
  </div>
</form>
```

关键属性：

- `class="layui-form"`：启用 Layui 表单样式和渲染。
- `lay-filter`：表单事件、赋值、取值的标识。
- `lay-submit`：声明提交按钮。
- `lay-verify`：声明验证规则。

## 核心 API

| API | 参数 | 返回 | 说明 |
| --- | --- | --- | --- |
| `form.render(type, filter)` | `string?, string?` | - | 渲染全部或指定类型表单元素 |
| `form.verify(obj)` | `object` | - | 自定义验证规则 |
| `form.validate(elem)` | `selector/DOM/jQuery` | `boolean` | 主动触发表单验证 |
| `form.val(filter, obj)` | `string, object?` | `object/undefined` | 表单赋值或取值 |
| `form.submit(filter, callback)` | `string, function` | - | 主动提交指定表单 |
| `form.on(event, callback)` | `string, function` | - | 监听提交、选择、复选、单选等事件 |
| `form.set(options)` | `object` | - | 设置 form 全局配置 |
| `form.config` | - | `object` | 获取全局配置 |

## form.render(type, filter)

用于渲染动态插入或修改后的表单元素。

| 参数 | 类型 | 说明 |
| --- | ---: | --- |
| `type` | `string` | 可选。指定渲染类型，如 `select`、`checkbox`、`radio` |
| `filter` | `string` | 可选。只渲染指定 `lay-filter` 容器内的表单 |

示例：

```js
form.render();
form.render('select');
form.render(null, 'userForm');
```

AI 规则：动态插入 `select`、`checkbox`、`radio` 后，必须调用 `form.render()`。

## lay-verify 验证规则

内置常用规则：

- `required`：必填
- `phone`：手机号
- `email`：邮箱
- `url`：URL
- `number`：数字
- `date`：日期
- `identity`：身份证

多个规则可用 `|` 分隔：

```html
<input name="email" lay-verify="required|email" class="layui-input">
```

自定义规则：

```js
form.verify({
  username: function(value, elem){
    if (value.length < 3) {
      return '用户名至少 3 个字符';
    }
  },
  pass: [/^[\S]{6,16}$/, '密码必须 6 到 16 位，且不能出现空格']
});
```

回调返回字符串表示验证失败；不返回表示通过。

## form.val(filter, obj)

### 赋值

```js
form.val('userForm', {
  username: 'alice',
  status: 'enabled',
  role: 'admin'
});
```

### 取值

```js
var values = form.val('userForm');
console.log(values);
```

规则：表单元素必须有 `name` 属性，否则无法进入字段集合。

## 提交事件

```js
form.on('submit(userSubmit)', function(data){
  console.log(data.elem);  // 当前提交按钮
  console.log(data.form);  // 当前 form DOM
  console.log(data.field); // 表单字段对象

  // Ajax 提交
  return false; // 阻止原生表单跳转
});
```

回调对象：

| 字段 | 类型 | 说明 |
| --- | ---: | --- |
| `data.elem` | `DOM` | 当前触发提交的按钮 |
| `data.form` | `DOM` | 当前表单元素 |
| `data.field` | `object` | 表单字段键值对象 |

AI 规则：所有 Layui 表单提交示例都必须 `return false`，避免页面跳转。

## 主动提交

```js
form.submit('userSubmit', function(data){
  console.log(data.field);
});
```

用于按钮不在表单内部，或需要 JS 主动触发提交的场景。

## 常用事件

| 事件 | 写法 | 说明 |
| --- | --- | --- |
| 表单提交 | `form.on('submit(filter)', callback)` | 提交按钮触发 |
| 选择框 | `form.on('select(filter)', callback)` | select 选择后触发 |
| 复选框 | `form.on('checkbox(filter)', callback)` | checkbox 改变后触发 |
| 开关 | `form.on('switch(filter)', callback)` | switch 风格 checkbox 改变后触发 |
| 单选框 | `form.on('radio(filter)', callback)` | radio 选中后触发 |
| 输入框点缀 | `form.on('input-affix(filter)', callback)` | 点击输入框动态点缀图标触发 |

## 完整示例

```html
<form class="layui-form" lay-filter="userForm">
  <div class="layui-form-item">
    <label class="layui-form-label">用户名</label>
    <div class="layui-input-block">
      <input type="text" name="username" lay-verify="required|username" class="layui-input">
    </div>
  </div>

  <div class="layui-form-item">
    <label class="layui-form-label">状态</label>
    <div class="layui-input-block">
      <select name="status" lay-verify="required">
        <option value="">请选择</option>
        <option value="enabled">启用</option>
        <option value="disabled">禁用</option>
      </select>
    </div>
  </div>

  <div class="layui-form-item">
    <div class="layui-input-block">
      <button class="layui-btn" lay-submit lay-filter="userSubmit">保存</button>
      <button type="reset" class="layui-btn layui-btn-primary">重置</button>
    </div>
  </div>
</form>

<script>
layui.use(function(){
  var form = layui.form;
  var layer = layui.layer;

  form.verify({
    username: function(value){
      if (value.length < 3) return '用户名至少 3 个字符';
    }
  });

  form.on('submit(userSubmit)', function(data){
    layer.msg('提交成功：' + JSON.stringify(data.field));
    return false;
  });
});
</script>
```

## AI 使用注意事项

- 任何字段都必须写 `name`。
- 表单容器必须写 `class="layui-form"`。
- 需要通过 `form.val()` 操作的表单必须写 `lay-filter`。
- 动态插入表单元素后必须 `form.render()`。
- 提交事件必须 `return false`。
- 复杂验证必须用 `form.verify()`，不要只写 HTML5 原生校验。

## 文档地址

- `https://layui.dev/docs/2/form/`
