# 分页组件

## 作用

`laypage` 提供纯前端分页渲染能力，适合列表页、搜索结果页、日志页和自定义分页数据源。

## API

- `laypage.render(options)`

## 常用参数

| 参数 | 类型 | 说明 |
| --- | --- | --- |
| `elem` | `string/DOM` | 分页容器 |
| `count` | `number` | 总条数 |
| `limit` | `number` | 每页数量 |
| `curr` | `number` | 当前页 |
| `groups` | `number` | 连续页码个数 |
| `first` | `string/boolean` | 首页按钮文本或开关 |
| `last` | `string/boolean` | 尾页按钮文本或开关 |
| `layout` | `array` | 显示项组合 |
| `theme` | `string` | 主题颜色 |
| `jump` | `function` | 页码切换回调 |

## 常见场景

- 后端返回总数后自行分页
- 无限列表配合前端切片
- 数据表格外部独立分页

## 典型示例

```html
<div id="page"></div>
```

```js
layui.use(function(){
  var laypage = layui.laypage;
  laypage.render({
    elem: 'page',
    count: 320,
    limit: 10,
    curr: 1,
    layout: ['count', 'prev', 'page', 'next', 'limit', 'refresh', 'skip'],
    jump: function(obj, first){
      if (!first) {
        console.log('切换到第', obj.curr, '页');
      }
    }
  });
});
```

## AI 使用规则

- `count` 必须是总条数，不是总页数。
- 如果分页只是表格内置场景，优先用 `table` 的分页，不要重复造轮子。
- `jump` 回调中要区分首次渲染与用户切页。

## 常见参数组合

- 简洁模式：`['prev', 'page', 'next']`
- 带统计：`['count', 'prev', 'page', 'next', 'skip']`
- 带限制切换：`['count', 'prev', 'page', 'next', 'limit', 'refresh', 'skip']`

## 文档地址

- `https://layui.dev/docs/2/`
