# Layui 2.13.6 框架使用总览

## 定位

Layui 是轻量级 Web UI 组件库。

本文件用于指导 AI 使用 Layui 2.13.6 开发前端页面。

## 版本

- 官方框架版本：`2.13.6`
- 技能版本：`2.13.6`
- 文档入口：`https://layui.dev/docs/2/`

## 核心特点

- 原生态开发
- 轻量级模块化
- 组件化但不依赖复杂构建工具
- 风格简约，适合中后台与快速原型

## 引入方式

- 官网下载
- Git 下载
- npm 下载
- 第三方 CDN

基础引用文件：

- `layui.css`：核心样式库
- `layui.js`：核心模块库

## 标准页面骨架

```html
<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Layui Page</title>
  <link href="/layui/css/layui.css" rel="stylesheet">
</head>
<body>
  <div class="layui-container layui-padding-3">
    <!-- 页面内容 -->
  </div>

  <script src="/layui/layui.js"></script>
  <script>
  layui.use(function(){
    var $ = layui.$;
    var layer = layui.layer;
    var form = layui.form;

    // 初始化和事件绑定
  });
  </script>
</body>
</html>
```

## 模块使用规则

- 推荐：`layui.use(function(){ ... })`
- 指定模块：`layui.use(['table', 'form'], function(){ ... })`
- 常用模块：`layui.$` / `layui.layer` / `layui.form` / `layui.table` / `layui.element` / `layui.laydate` / `layui.upload` / `layui.dropdown` / `layui.util`

## 生成页面时的 AI 规则

- 明确模块
- 提供 HTML 容器和 `id` / `lay-filter`
- 事件使用官方语法
- 动态 DOM 后重新渲染
- 说明回调入参和返回值
- 说明接口返回格式或前端数据格式
- 不混用其他 UI 框架
- 不默认引入构建工具

## 常用组合

### 管理后台列表页

- `layout/grid`
- `form` 搜索条件
- `table` 数据表格
- `laypage` 或 table 内置分页
- `dropdown` 行操作菜单
- `layer` 新增/编辑/确认删除

### 表单编辑页

- `form`
- `input/select/checkbox/radio`
- `laydate`
- `upload`
- `layer` 提示与确认

### 控制台布局

- `layout`
- `nav` 或 `menu`
- `tabs`
- `card/panel`
- `table`

## 生产注意事项

- CDN 示例只用于演示，生产项目推荐下载到本地静态目录。
- 使用图标字体跨域时，静态资源服务器需要正确配置跨域头。
- IE 兼容场景需要按官方说明添加 polyfill 或响应式补丁。
- 2.10+ 推荐使用 `tabs` 替代旧 `tab`。
- 2.12+ 国际化优先使用 `LAYUI_GLOBAL.i18n` 在 `layui.js` 之前配置。

## 主目录导航

- 基础：开始使用、底层方法、模块系统、组件构建、国际化、更新日志
- 布局：框体、栅格
- 通用：颜色、按钮、图标、动画
- 表单：表单组件、输入框、选择框、复选框、单选框
- 展示：表格、分页、树形表格、导航菜单、菜单、标签页、选项卡、徽章、辅助、公共类、面板、进度条、时间线、固定条、树、轮播、流加载、代码预览
- 交互：弹出层、日期时间、上传、下拉菜单、颜色选择器、穿梭框、滑块、评分
- 其他：模板引擎、工具模块

## 推荐加载顺序

1. `layui.css`
2. `layui.js`
3. 需要的模块通过 `layui.use()` 按需加载

## 关键约定

- Layui 组件普遍支持 `lay-filter`
- 许多组件支持 `lay-options`
- 组件实例、API、事件大多通过 `layui.xxx` 模块访问
- `lay-filter` 用于事件绑定与组件定位，复杂页面必须显式设置。
- `lay-options` 可把部分 JS 配置写到 HTML 属性上，但复杂配置仍建议放到 JS。
