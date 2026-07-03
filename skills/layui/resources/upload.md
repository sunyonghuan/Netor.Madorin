# 上传组件

## 作用

提供文件上传、图片上传、多文件上传、拖拽上传、自动上传和手动触发上传等能力。

## API

- `upload.render(options)`
- `inst.upload()`
- `inst.reload(options)`

## 常用属性

| 属性 | 说明 |
| --- | --- |
| `elem` | 触发元素 |
| `url` | 上传接口 |
| `field` | 文件字段名 |
| `data` | 附加参数 |
| `choose` | 选择文件回调 |
| `before` | 上传前回调 |
| `progress` | 上传进度回调 |
| `done` | 成功回调 |
| `error` | 失败回调 |
| `accept` | 文件类型限制 |
| `multiple` | 多文件上传 |
| `drag` | 拖拽上传 |
| `auto` | 是否自动上传 |
| `bindAction` | 绑定上传动作按钮 |

## 常见场景

- 头像上传
- 图片批量上传
- 附件上传
- 表单提交前文件收集

## 典型回调

| 回调 | 说明 |
| --- | --- |
| `choose` | 文件选择完成后触发 |
| `before` | 请求发送前触发，可中断上传 |
| `progress` | 上传进度 |
| `done` | 服务端返回成功 |
| `error` | 失败 |

## AI 使用规则

- 先明确后端接口字段名，再写 `field` 和 `data`。
- 文件上传应说明是否需要鉴权头和 CSRF 处理。
- 图片上传要说明预览与回填逻辑。
- 手动上传时，必须保留实例对象并在按钮点击时调用 `inst.upload()`。

## 示例骨架

```html
<button type="button" class="layui-btn" id="uploadBtn">上传文件</button>
```

```js
layui.use(function(){
  var upload = layui.upload;
  upload.render({
    elem: '#uploadBtn',
    url: '/api/upload',
    accept: 'file'
  });
});
```

## 文档地址

- `https://layui.dev/docs/2/`
