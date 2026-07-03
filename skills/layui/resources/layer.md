# 弹出层组件 layer

## 作用

消息提示、确认框、页面弹层、iframe 弹层、加载层、tips、prompt、图片预览。

## 模块加载

```js
layui.use(function(){
  var layer = layui.layer;
});
```

## 核心 API

| API | 参数 | 返回 | 说明 |
| --- | --- | --- | --- |
| `layer.open(options)` | `object` | `number` | 打开弹层，核心方法，返回层索引 |
| `layer.alert(content, options, yes)` | `string, object?, function?` | `number` | 信息框 |
| `layer.confirm(content, options, yes, cancel)` | `string, object?, function?, function?` | `number` | 询问框 |
| `layer.msg(content, options, end)` | `string, object?, function?` | `number` | 短提示 |
| `layer.load(icon, options)` | `number?, object?` | `number` | 加载层 |
| `layer.tips(content, elem, options)` | `string, selector/DOM, object?` | `number` | tips 提示 |
| `layer.prompt(options, yes)` | `object, function` | `number` | 输入框层 |
| `layer.photos(options)` | `object` | `number` | 图片层 |
| `layer.tab(options)` | `object` | `number` | 标签页层 |
| `layer.close(index, callback)` | `number, function?` | - | 关闭指定层 |
| `layer.closeAll(type, callback)` | `string?, function?` | - | 关闭全部或指定类型层 |

## layer.open(options) 常用参数

| 参数 | 类型 | 默认值 | 说明 |
| --- | ---: | --- | --- |
| `type` | `number` | `0` | 弹层类型：0 信息框，1 页面层，2 iframe，3 loading，4 tips |
| `title` | `string/array/boolean` | - | 标题，`false` 表示不显示标题 |
| `content` | `string/DOM/jQuery` | - | 内容。`type:2` 时通常为 iframe URL |
| `area` | `string/array` | `auto` | 宽高，如 `['800px','600px']` |
| `offset` | `string/array` | `auto` | 坐标位置 |
| `icon` | `number` | - | 图标，常用于 alert/msg/confirm |
| `btn` | `array/string` | - | 按钮文本 |
| `shade` | `number/array/boolean` | `0.3` | 遮罩透明度或颜色 |
| `shadeClose` | `boolean` | `false` | 点击遮罩是否关闭 |
| `closeBtn` | `number/boolean` | `1` | 关闭按钮样式或隐藏 |
| `time` | `number` | `0` | 自动关闭毫秒数，0 表示不自动关闭 |
| `anim` | `number` | - | 弹出动画 |
| `isOutAnim` | `boolean` | `true` | 是否开启关闭动画 |
| `maxmin` | `boolean` | `false` | 是否显示最大化/最小化 |
| `resize` | `boolean` | `true` | 是否允许拉伸 |
| `zIndex` | `number` | - | 层级 |
| `success` | `function` | - | 弹层打开成功回调 |
| `yes` | `function` | - | 第一个按钮回调 |
| `btn2` | `function` | - | 第二个按钮回调 |
| `cancel` | `function` | - | 右上角关闭回调 |
| `end` | `function` | - | 弹层销毁后回调 |

## 弹层类型 type

| type | 名称 | 典型用途 |
| ---: | --- | --- |
| `0` | dialog 信息框 | alert、confirm、msg 的基础 |
| `1` | page 页面层 | 弹出页面片段、表单、DOM 内容 |
| `2` | iframe 层 | 弹出一个独立 URL 页面 |
| `3` | loading 加载层 | Ajax 请求等待 |
| `4` | tips 贴士层 | 元素旁边提示 |

## 常用示例

### 消息提示

```js
layer.msg('保存成功', { icon: 1, time: 1500 });
```

### 删除确认

```js
layer.confirm('确定删除该数据？', { icon: 3, title: '确认' }, function(index){
  // 执行删除
  layer.close(index);
});
```

### 页面层表单

```html
<script type="text/html" id="userEditTpl">
  <form class="layui-form layui-padding-3" lay-filter="editForm">
    <input type="text" name="username" placeholder="用户名" class="layui-input">
  </form>
</script>
```

```js
var index = layer.open({
  type: 1,
  title: '编辑用户',
  area: ['520px', '300px'],
  content: layui.$('#userEditTpl').html(),
  btn: ['保存', '取消'],
  success: function(layero, index){
    var form = layui.form;
    form.render();
    form.val('editForm', { username: 'alice' });
  },
  yes: function(index, layero){
    var form = layui.form;
    var values = form.val('editForm');
    console.log(values);
    layer.close(index);
  }
});
```

### iframe 层

```js
layer.open({
  type: 2,
  title: '详情',
  area: ['900px', '650px'],
  content: '/user/detail?id=1'
});
```

### 加载层

```js
var loading = layer.load(2, { shade: 0.2 });
// Ajax 完成后
layer.close(loading);
```

### Prompt 输入

```js
layer.prompt({
  title: '请输入原因',
  formType: 2,
  maxlength: 200
}, function(value, index, elem){
  console.log(value);
  layer.close(index);
});
```

## prompt 私有参数

| 参数 | 类型 | 默认值 | 说明 |
| --- | ---: | --- | --- |
| `formType` | `number/string` | `0` | 0 文本，1 密码，2 多行文本，也支持 input 类型 |
| `value` | `string` | - | 初始值 |
| `maxlength` | `number` | `500` | 最大字符数 |
| `placeholder` | `string` | - | 占位符 |

## 回调参数说明

### success(layero, index)

- `layero`：当前弹层容器 jQuery 对象。
- `index`：当前层索引。

### yes(index, layero, that)

- `index`：当前层索引。
- `layero`：当前弹层容器。
- `that`：当前按钮元素。

### cancel(index, layero)

右上角关闭按钮触发。返回 `false` 可阻止关闭。

## AI 使用注意事项

- 删除、危险操作必须使用 `layer.confirm`。
- Ajax 请求期间建议使用 `layer.load`，完成后必须关闭 loading。
- 弹出表单后，若内容是动态 HTML，必须调用 `form.render()`。
- 页面层 `type: 1` 的 `content` 可为 HTML 字符串或 DOM 内容。
- iframe 层 `type: 2` 的 `content` 应为 URL。
- 需要访问 iframe 内容时使用 `layer.getChildFrame(selector, index)`。
- 关闭弹层必须使用 `layer.close(index)`，不要直接删除 DOM。

## 文档地址

- `https://layui.dev/docs/2/layer/`
