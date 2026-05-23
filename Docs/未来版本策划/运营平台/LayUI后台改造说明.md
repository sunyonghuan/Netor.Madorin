# LayUI 后台改造说明

发布日期：2026-05-08

## 目标

将 `Netor.Madorin.Platform.Admin` 后台界面改造成基于 LayUI 的管理后台，并尽量复用 `Netor.Operates.Admin` 的本地资源与布局结构。

## 改造范围

### 1. 主布局

- 将后台主布局改为标准 LayUI 管理后台结构：
  - `layui-layout layui-layout-admin`
  - `layui-header`
  - `layui-side`
  - `layui-body`
- 左侧菜单改为 LayUI 竖向导航：
  - `layui-nav`
  - `layui-nav-tree`
  - `layui-nav-side`
- 统一使用本地 `wwwroot/lib/layui` 资源，避免 CDN 依赖导致样式失效。

### 2. 页面资源

- 后台页统一引用本地：
  - `~/lib/layui/css/layui.css`
  - `~/lib/layui/layui.js`
- 登录页也同步切换为本地 LayUI 资源。
- `site.js` 中统一执行：
  - `element.render()`
  - `form.render()`

### 3. 表单与列表页

- 列表页、详情页、编辑页逐步替换为 LayUI 风格：
  - `layui-form`
  - `layui-input`
  - `layui-select`
  - `layui-table`
  - `layui-btn`
- 表格右侧操作区改为紧凑按钮组，减少按钮间距。

### 4. 统计栏优化

- 各列表页顶部统计栏压缩高度。
- 将统计卡片调整为更紧凑的概览样式，减少对列表区域的占用。
- 页面主体 padding 收紧，提升列表可视空间。

## 已完成文件

### 布局与导航

- `Views/Shared/_Layout.cshtml`
- `Views/Shared/_NavPartial.cshtml`
- `Views/Shared/_ContentHeader.cshtml`

### 资源与脚本

- `wwwroot/css/site.css`
- `wwwroot/js/site.js`
- `Views/Auth/Login.cshtml`
- `wwwroot/lib/layui/**`

### 业务页

- `Views/Assets/Index.cshtml`
- `Views/Assets/Details.cshtml`
- `Views/Settings/Index.cshtml`
- `Views/Settings/Edit.cshtml`
- `Views/Subscriptions/Index.cshtml`
- `Views/Subscriptions/Downloads.cshtml`
- `Views/Orders/Index.cshtml`
- `Views/Orders/Details.cshtml`
- `Views/Orders/Transactions.cshtml`
- `Views/Accounts/Index.cshtml`
- `Views/Accounts/Details.cshtml`
- `Views/Accounts/Edit.cshtml`
- `Views/Accounts/Recharge.cshtml`
- `Views/Accounts/Password.cshtml`
- `Views/Accounts/Status.cshtml`
- `Views/Categories/Index.cshtml`
- `Views/Categories/Create.cshtml`
- `Views/Categories/Edit.cshtml`
- `Views/Categories/_CategoryForm.cshtml`
- `Views/Home/Index.cshtml`

## 验证结果

### 编译

已通过 `dotnet build`。

### 本地资源

以下资源已确认可访问：

- `/lib/layui/css/layui.css`
- `/lib/layui/layui.js`
- `/js/site.js`

### 页面渲染

已在浏览器中验证：

- LayUI 资源加载正常
- 左侧竖向导航生效
- 表单组件可正确渲染
- 分类页全选逻辑可用
- 统计栏高度已压缩，列表区域更大

## 说明

本次改造重点不是简单替换 class 名称，而是恢复 LayUI 的标准布局和资源加载路径，避免“样式看起来像 LayUI，但实际并未加载”的问题。

## 后续建议

1. 继续统一各业务页的按钮组合和表格间距。
2. 进一步压缩顶部统计栏，优先保障列表可视区域。
3. 对详情页和表单页继续做 LayUI 化收敛，减少 Bootstrap 混用。
