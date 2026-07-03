---
name: layui
description: Machine-readable Layui skill for building front-end pages. Use it to install, load, initialize, and develop with Layui components.
version: "1.0.0"
framework-version: "2.13.6"
license: MIT
user-invocable: true
---

# Layui 技能

## Overview

用途：AI 直接按 Layui 2.13.6 生成可运行前端页面。

## 处理顺序

1. 读 `resources/framework-overview.md`
2. 读对应组件文件
3. 生成完整 HTML + JS + 初始化
4. 补齐参数类型、默认值、事件、回调、数据结构
5. 补齐版本差异和兼容性
6. 优先使用 Layui 原生组件和模块系统

## 触发场景

| 关键词 | 说明 |
| --- | --- |
| `layui` | 使用 Layui 构建页面、组件、交互 |
| `layui 表格` | 读取 `resources/table.md` 并生成 table 页面 |
| `layui 表单` | 读取 `resources/form.md` 及表单子组件文件 |
| `layui 弹层` | 读取 `resources/layer.md` 并生成弹层交互 |
| `layui 上传` | 读取 `resources/upload.md` 并生成上传功能 |

## 使用规则

- 生成前先确认页面目标、组件组合、数据来源、事件、回显、异步加载
- 先总览，再基础模块，再核心组件，再业务组件
- 表单类：`form.md` -> `input.md` / `select.md` / `checkbox.md` / `radio.md`
- 展示类：`table.md` / `treeTable.md` / `tabs.md` / `nav.md` / `menu.md`
- 交互类：`layer.md` / `laydate.md` / `upload.md` / `dropdown.md` / `colorpicker.md` / `transfer.md` / `slider.md` / `rate.md`
- 页面必须包含 HTML 结构、CSS/JS 引入、`layui.use()` 初始化、渲染或事件绑定

## 安装与引入

### 最小页面骨架

```html
<!doctype html>
<html>
<head>
	<meta charset="utf-8">
	<meta name="viewport" content="width=device-width, initial-scale=1">
	<link rel="stylesheet" href="/layui/css/layui.css">
</head>
<body>

	<div class="layui-container">
		<h1 class="layui-font-16">示例页面</h1>
	</div>

	<script src="/layui/layui.js"></script>
	<script>
	layui.use(function(){
		var layer = layui.layer;
		layer.msg('Layui loaded');
	});
	</script>
</body>
</html>
```

### 安装要求

- 静态引入 `layui.css` 和 `layui.js`。
- 如果是生产环境，优先使用本地静态资源。
- 如果页面只用局部模块，也必须保留标准入口结构。

## 代码生成规则

- 默认使用浏览器直引模式：`layui.css` + `layui.js`。
- 示例可使用 CDN，但生产项目应替换为本地静态资源。
- 所有 JS 初始化放入 `layui.use(function(){ ... })`。
- 需要模块时从 `layui` 对象取值，例如：`var table = layui.table; var form = layui.form;`。
- 动态插入组件后必须调用对应渲染方法，例如 `form.render()`、`element.render()`。
- 事件绑定必须使用组件官方事件语法，例如 `form.on('submit(filter)', callback)`、`table.on('tool(filter)', callback)`。
- 参数写法应说明类型、默认值、适用范围，不允许只给 API 名称。
- 若组件支持实例对象，应保留实例变量，方便后续 `reload()`、`setValue()`、`goto()` 等操作。
- 若组件存在新旧替代关系，优先用新组件。例如标签页优先用 `tabs`，旧 `tab` 仅作为兼容方案。
- 示例必须包含必要的 HTML、初始化代码、回调处理、数据结构说明，必要时补充接口返回格式。

## 引用文件结构

- `resources/framework-overview.md`：框架总览
- `resources/getting-started.md`：开始使用
- `resources/base.md`：底层方法
- `resources/modules.md`：模块系统
- `resources/component.md`：组件构建器
- `resources/i18n.md`：国际化
- `resources/versions.md`：更新日志
- `resources/layout.md`：框体布局
- `resources/grid.md`：栅格布局
- `resources/color.md`：颜色
- `resources/button.md`：按钮
- `resources/icon.md`：图标
- `resources/anim.md`：动画
- `resources/form.md`：表单组件总览
- `resources/input.md`：输入框 / Textarea
- `resources/select.md`：选择框
- `resources/checkbox.md`：复选框
- `resources/radio.md`：单选框
- `resources/table.md`：表格
- `resources/laypage.md`：分页
- `resources/treeTable.md`：树形表格
- `resources/nav.md`：导航菜单
- `resources/menu.md`：基础菜单
- `resources/tabs.md`：标签页 Tabs
- `resources/tab.md`：选项卡 Tab
- `resources/badge.md`：徽章
- `resources/auxiliar.md`：辅助元素
- `resources/class.md`：公共类
- `resources/panel.md`：面板 / Card / Collapse
- `resources/progress.md`：进度条
- `resources/timeline.md`：时间线
- `resources/fixbar.md`：固定条
- `resources/tree.md`：树组件
- `resources/carousel.md`：轮播
- `resources/flow.md`：流加载
- `resources/code.md`：代码预览组件
- `resources/layer.md`：弹出层
- `resources/laydate.md`：日期与时间选择器
- `resources/upload.md`：上传
- `resources/dropdown.md`：下拉菜单
- `resources/colorpicker.md`：颜色选择器
- `resources/transfer.md`：穿梭框
- `resources/slider.md`：滑块
- `resources/rate.md`：评分
- `resources/laytpl.md`：模板引擎
- `resources/util.md`：工具模块

## 组件使用原则

1. 复杂页面优先采用：`layout/grid` 做结构，`form/table/layer` 做业务交互。
2. 表格页面常用组合：`table + form + laypage + dropdown + layer`。
3. 表单页面常用组合：`form + input/select/checkbox/radio + laydate + upload + layer`。
4. 后台布局常用组合：`layout + nav/menu + tabs + table + layer`。
5. 列表无限加载使用 `flow`；传统分页使用 `laypage` 或 table 内置分页。
6. 代码展示/文档页面使用 `code`。
7. 国际化需求先配置 `LAYUI_GLOBAL.i18n`，再引入 `layui.js`。

## 文档来源

- 官方文档：`https://layui.dev/docs/2/`
- 更新日志：`https://layui.dev/docs/2/versions.html`
- 组件与模块页：对应组件路径
