# 底层方法

## 范围

Layui 提供的一组基础 API，供组件与业务代码共用，适合模块管理、事件管理、工具调用和浏览器能力封装。

## 主要 API

- `layui.config(options)`：全局配置
- `layui.define([modules], callback)`：定义模块
- `layui.use([modules], callback)`：使用模块
- `layui.extend(obj)`：扩展模块
- `layui.disuse([modules])`：弃用模块
- `layui.link(href)`：加载 CSS
- `layui.each(obj, callback)`：遍历
- `layui.type(operand)`：类型判断
- `layui.sort(obj, key, desc)`：数组对象排序
- `layui.url(href)`：链接解析
- `layui.data(table, settings)`：localStorage 封装
- `layui.sessionData(table, settings)`：sessionStorage 封装
- `layui.device(key)`：浏览器信息
- `layui.stope(e)`：阻止冒泡
- `layui.onevent(modName, events, callback)`：注册事件
- `layui.event(modName, events, params)`：触发事件
- `layui.off(events, modName)`：移除事件
- `layui.debounce(fn, wait)`：防抖
- `layui.throttle(fn, wait)`：节流
- `layui.factory(modName)`：获取模块工厂
- `layui.hint()`：控制台提示

## 使用建议

- 全局配置应尽早执行，最好在模块使用前完成。
- 事件系统适合跨模块通信，不要直接用来代替组件回调。

## AI 使用规则

- 编写页面时应先明确是否需要全局配置、模块扩展或本地存储封装。
- `debounce` 与 `throttle` 适合搜索、滚动、输入联想等场景。

## 文档地址

- `https://layui.dev/docs/2/`
