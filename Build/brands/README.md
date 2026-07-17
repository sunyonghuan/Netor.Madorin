# 品牌发布目录

本目录管理所有品牌的发布配置与资产。每个品牌对应一个同名子文件夹。

---

## 目录结构

```
brands/
  publish.ps1          ← 统一发布入口（传 -Brand 参数执行）
  README.md            ← 本文档
  madorin/
    madorin.json       ← 品牌配置
    brand.ico          ← 应用图标（用于 UI 主程序 + NativeHost）
    brand.png          ← 应用 Logo（用于界面内显示）
  sreamx/
    sreamx.json
    brand.ico
    brand.png
```

**资产命名约定**：每个品牌文件夹下的图标/Logo 统一命名为 `brand.ico` / `brand.png`。
发布时脚本会自动将它们覆盖到项目源码的对应位置，发布完成后恢复默认值。

---

## 发布命令

在 `Build/` 目录下执行（PowerShell）：

```powershell
# 校验配置，不发布
powershell -NoProfile -ExecutionPolicy Bypass -File .\brands\publish.ps1 -Brand madorin -ValidateOnly

# 发布
powershell -NoProfile -ExecutionPolicy Bypass -File .\brands\publish.ps1 -Brand madorin -UseVsWhereFix

# 发布 + 打包 zip/sha256
powershell -NoProfile -ExecutionPolicy Bypass -File .\brands\publish.ps1 -Brand sreamx -UseVsWhereFix -Package
```

---

## 新增品牌

1. 在 `brands/` 下新建文件夹，命名为品牌 ID（小写英文）。
2. 在文件夹内放置：
   - `{brand}.json` — 复制 `madorin.json` 修改各字段
   - `brand.ico` — 品牌图标（256×256 多尺寸 `.ico`）
   - `brand.png` — 品牌 Logo（建议 512×512 透明背景 PNG）
3. 执行 `-ValidateOnly` 验证配置无误后再正式发布。

---

## 品牌配置字段说明（`{brand}.json`）

### 标识类

| 字段 | 必填 | 说明 |
|------|------|------|
| `id` | ✅ | 品牌唯一标识符，小写英文，与文件夹名一致。例：`madorin`。 |
| `englishName` | ✅ | 品牌英文名称，用于日志输出和默认值推导。例：`Madorin`。 |
| `displayName` | ✅ | 应用显示名称（本地化），出现在标题栏、关于窗口等。例：`玛得令`。 |
| `assistantSubtitle` | | AI 助手的副标题，显示在主界面助手名称下方。默认：`{englishName} AI Assistant`。 |
| `websiteHost` | | 品牌官网域名（不含协议），用于生成 `WebsiteBaseUrl`。例：`madorin.netor.me`。 |

### 程序集 / 可执行文件类

| 字段 | 必填 | 说明 |
|------|------|------|
| `desktopAssemblyName` | | 桌面端主程序集名称，决定 `.exe` 文件名。例：`Madorin` → `Madorin.exe`。默认值等于 `englishName`。 |
| `assemblyTitle` | | 程序集标题元数据（文件属性中显示的"文件说明"）。默认值等于 `englishName`。 |
| `resourceAssemblyName` | | Avalonia 资源程序集名称，**必须与 `desktopAssemblyName` 保持一致**，否则运行时找不到图标和图片资源。默认值等于 `englishName`。 |
| `nativeHostAssemblyName` | | NativeHost 插件宿主程序集名称，决定 NativeHost `.exe` 文件名。例：`Madorin.NativeHost` → `Madorin.NativeHost.exe`。默认：`{englishName}.NativeHost`。 |

### 发布 / 打包类

| 字段 | 必填 | 说明 |
|------|------|------|
| `releaseDirectoryName` | | 发布输出目录名称，位于 `Realases/` 下。例：`Madorin` → `Realases/Madorin/`。默认值等于 `englishName`。 |
| `packageName` | | 打包时生成的 zip 文件名前缀。例：`Madorin` → `Madorin-v1.3.10-win-x64.zip`。默认值等于 `englishName`。 |

### 资产类（固定值，勿修改）

| 字段 | 值 | 说明 |
|------|-----|------|
| `applicationIconPath` | `Assets\brand.ico` | UI 主程序图标路径（相对 UI 项目目录），由构建脚本从本品牌文件夹同步。 |
| `nativeHostIcon` | `brand.ico` | NativeHost 图标文件名（相对 NativeHost 项目目录），由构建脚本同步。 |
| `logoImageFileName` | `brand.png` | 应用 Logo 文件名，运行时通过 Avalonia 资源加载，出现在聊天界面、会议模式、关于窗口等。 |

> 上述三个字段值已固定为规范名，**新增品牌时直接复制，无需修改**。实际内容由对应的 `brand.ico` / `brand.png` 文件决定。

### 平台 API 类

| 字段 | 必填 | 说明 |
|------|------|------|
| `platformBaseUrlSettingKey` | | 用户设置中平台 API 地址的 Key 名称。默认：`Platform.BaseUrl`。通常无需修改。 |
| `localPlatformApiBaseUrl` | | 本地联调时的平台 API 地址。默认：`http://localhost:5190`。 |
| `productionPlatformApiBaseUrl` | | 生产环境平台 API 地址，打包后应用默认连接此地址。例：`https://platform.netor.me`。 |
| `platformBaseUrlDescription` | | 应用内设置页面中该 API 地址选项的描述文本，面向最终用户展示。 |
