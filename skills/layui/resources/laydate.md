# 日期与时间选择器

## 作用

提供年、月、日、时、分、秒、范围、日期时间组合和日历展示能力，适合搜索条件、表单编辑和数据筛选。

## API

- `laydate.render(options)`
- `laydate.hint(id, opts)`
- `laydate.getInst(id)`
- `laydate.unbind(id)`
- `laydate.close(id)`
- `laydate.getEndDate(month, year)`

## 常用属性

| 属性 | 说明 |
| --- | --- |
| `elem` | 绑定元素 |
| `type` | 类型，如日期、时间、日期时间、年月、范围等 |
| `id` | 实例标识 |
| `position` | 弹出位置 |
| `zIndex` | 层级 |
| `btns` | 底部按钮 |
| `autoConfirm` | 是否自动确认 |
| `lang` | 语言 |
| `theme` | 主题 |
| `calendar` | 是否显示日历信息 |

## 常见场景

- 搜索时间范围
- 订单下单时间
- 统计报表筛选
- 定时任务配置

## 常见模式

### 单日期

```js
laydate.render({ elem: '#startDate' });
```

### 日期范围

```js
laydate.render({
  elem: '#rangeDate',
  range: true
});
```

### 日期时间

```js
laydate.render({
  elem: '#createdAt',
  type: 'datetime'
});
```

## AI 使用规则

- 搜索条件里优先使用范围模式，避免两个独立日期框造成逻辑碎片化。
- 需要回显时，先设置 `value` 再初始化。
- 不同页面的时间格式要统一，避免前后端解析差异。

## 示例骨架

```html
<input type="text" id="startDate" class="layui-input" placeholder="请选择日期">
```

## 文档地址

- `https://layui.dev/docs/2/`
