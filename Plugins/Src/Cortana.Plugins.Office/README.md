# Cortana.Plugins.Office

办公文档插件 — 提供 Word / Excel / PowerPoint 文档的创建、结构读取、内容编辑和另存操作。

## 项目结构

```
Cortana.Plugins.Office/
│
├── Startup.cs                       # 插件入口，[Plugin] 标记，DI 容器注册
├── PluginJsonContext.cs              # JSON 源码生成上下文（AOT 安全，所有序列化类型在此注册）
├── OfficePluginOptions.cs           # 插件配置选项（目录白名单）
├── ToolResult.cs                    # 统一返回结构 { success, code, message, data }
├── ErrorCodes.cs                    # 7 种错误码常量（与接口契约一致）
│
├── Models/                          # ── 数据模型层 ──
│   ├── ParagraphInfo.cs             #   段落摘要（index, style, text_preview, is_empty）
│   ├── DocumentResults.cs           #   创建/大纲/另存为 操作结果
│   ├── EditResults.cs               #   插入段落/删除段落/替换文本/插入表格 操作结果
│   ├── SheetInfo.cs                 #   工作表摘要（index, name, sheet_state, has_dimensions）
│   ├── WorkbookResults.cs           #   工作簿创建/列表/另存为 操作结果
│   ├── WorkbookEditResults.cs       #   读写区域/行操作/新增工作表 操作结果
│   ├── SlideInfo.cs                #   幻灯片摘要（index, layout_name, title_preview, body_preview, has_notes）
│   ├── PresentationResults.cs       #   演示文稿创建/幻灯片列表/另存为 操作结果
│   └── PresentationEditResults.cs   #   添加幻灯片/更新标题正文/删除幻灯片/更新备注 操作结果
│
├── Security/                        # ── 安全层 ──
│   ├── IDocumentPathGuard.cs        #   路径守卫接口
│   └── DocumentPathGuard.cs         #   实现：白名单校验 + 路径穿越防护
│
├── Infrastructure/                  # ── 基础设施层 ──
│   ├── DocxTemplate.cs              #   最小有效 docx 的 XML 模板常量
│   ├── DocxXmlHelper.cs            #   ZIP 条目读写 + Open XML 元素构造辅助
│   ├── XlsxTemplate.cs             #   最小有效 xlsx 的 XML 模板常量
│   ├── XlsxXmlHelper.cs            #   工作表 XML 读写 + 单元格操作辅助
│   ├── PptxTemplate.cs             #   最小有效 pptx 的 XML 模板常量
│   └── PptxXmlHelper.cs            #   幻灯片 XML 读写 + 占位符操作辅助
│
├── Services/                        # ── 服务层（业务逻辑，通过接口注入） ──
│   ├── IWordDocumentService.cs     #   Word 服务接口（7 个方法）
│   ├── WordDocumentService.cs       #   实现：创建 / 读取大纲 / 另存为
│   ├── WordDocumentService.Edit.cs #   实现：插入段落 / 删除段落 / 文本替换 / 插入表格
│   ├── IExcelWorkbookService.cs    #   Excel 服务接口（10 个方法）
│   ├── ExcelWorkbookService.cs     #   实现：工作簿创建 / 工作表列表 / 区域读写 / 另存为
│   ├── ExcelWorkbookService.Edit.cs #   实现：行操作 / 工作表增删
│   ├── IPowerPointService.cs      #   PowerPoint 服务接口（8 个方法）
│   └── PowerPointService.cs        #   实现：演示文稿创建 / 幻灯片列表 / 另存为
│   └── PowerPointService.Edit.cs   #   实现：添加幻灯片 / 更新标题正文 / 删除幻灯片 / 更新备注
│
└── Tools/                           # ── 工具层（AI 入口，参数校验 + 调用服务） ──
    ├── ToolParameterHelper.cs       #   框架参数适配：int→bool, string→string[]
    ├── OfficeWordDocumentTools.cs   #   create_document / get_outline / save_as
    ├── OfficeWordParagraphTools.cs  #   insert_paragraph / delete_paragraph / replace_text
    ├── OfficeWordTableTools.cs      #   insert_table
    ├── OfficeExcelWorkbookTools.cs  #   create_workbook / list_sheets / read_range / save_as
    ├── OfficeExcelSheetTools.cs     #   add_sheet / delete_sheet / rename_sheet
    ├── OfficeExcelRowTools.cs      #   insert_row / delete_row / get_row
    ├── OfficePptPresentationTools.cs #   create_presentation / list_slides / save_as
    ├── OfficePptSlideTools.cs       #   add_slide / delete_slide
    └── OfficePptElementTools.cs    #   update_slide_title / update_slide_body / update_slide_notes
```

## 分层职责

| 层 | 职责 | 依赖方向 |
|----|------|---------|
| **Tools** | 接收框架调用、参数校验、路径安全、调用 Service、返回 `ToolResult` | → Security, Services |
| **Services** | 文档读写业务逻辑，操作 ZIP/XML | → Infrastructure |
| **Infrastructure** | docx 包结构和 XML 元素的底层操作 | → BCL |
| **Security** | 路径白名单校验、穿越防护 | → OfficePluginOptions |
| **Models** | 纯数据记录，无逻辑 | 无依赖 |

## DI 注册（Startup.Configure）

```
IDocumentPathGuard   →  DocumentPathGuard    (Singleton)
IWordDocumentService → WordDocumentService  (Singleton)
IExcelWorkbookService → ExcelWorkbookService (Singleton)
IPowerPointService   → PowerPointService    (Singleton)
OfficePluginOptions                         (Singleton)
```

工具类（`[Tool]` 标记）由框架源码生成器自动注册为 Singleton。

## 设计原则

- **Native AOT 安全**：不使用反射、COM Interop 或动态代码生成。JSON 序列化通过 `PluginJsonContext` 源码生成完成。
- **直接操作 Office 包**：使用 `System.IO.Compression` + `System.Xml.Linq` 处理 ZIP/XML，无第三方依赖。
- **DI 容器驱动**：所有服务通过接口注入，方便测试和替换。
- **路径安全守卫**：所有文件操作经 `IDocumentPathGuard` 校验，防止路径穿越和越权访问。
- **写操作默认不覆盖**：所有写操作输出到新文件，仅 `overwrite=1` 时允许覆盖。
- **小文件原则**：最大文件约 130 行，partial class 拆分大类。

## V1 工具清单

### Word 工具

| 工具名 | 用途 | 工具类 |
|--------|------|--------|
| `office_word_create_document` | 创建空白或模板文档 | OfficeWordDocumentTools |
| `office_word_get_outline` | 读取文档结构摘要 | OfficeWordDocumentTools |
| `office_word_save_as` | 另存为 | OfficeWordDocumentTools |
| `office_word_insert_paragraph` | 插入段落 | OfficeWordParagraphTools |
| `office_word_delete_paragraph` | 删除段落 | OfficeWordParagraphTools |
| `office_word_replace_text` | 文本查找替换 | OfficeWordParagraphTools |
| `office_word_insert_table` | 插入表格 | OfficeWordTableTools |

### Excel 工具

| 工具名 | 用途 | 工具类 |
|--------|------|--------|
| `office_excel_create_workbook` | 创建空白或模板工作簿 | OfficeExcelWorkbookTools |
| `office_excel_list_sheets` | 读取工作表列表 | OfficeExcelWorkbookTools |
| `office_excel_read_range` | 读取单元格区域 | OfficeExcelWorkbookTools |
| `office_excel_write_range` | 写入单元格区域 | OfficeExcelWorkbookTools |
| `office_excel_save_as` | 另存为 | OfficeExcelWorkbookTools |
| `office_excel_add_sheet` | 新增工作表 | OfficeExcelSheetTools |
| `office_excel_delete_sheet` | 删除工作表 | OfficeExcelSheetTools |
| `office_excel_rename_sheet` | 重命名工作表 | OfficeExcelSheetTools |
| `office_excel_insert_row` | 插入行 | OfficeExcelRowTools |
| `office_excel_delete_row` | 删除行 | OfficeExcelRowTools |
| `office_excel_get_row` | 读取行数据 | OfficeExcelRowTools |

### PowerPoint 工具

| 工具名 | 用途 | 工具类 |
|--------|------|--------|
| `office_ppt_create_presentation` | 创建空白或模板演示文稿 | OfficePptPresentationTools |
| `office_ppt_list_slides` | 读取幻灯片列表 | OfficePptPresentationTools |
| `office_ppt_save_as` | 另存为 | OfficePptPresentationTools |
| `office_ppt_add_slide` | 插入新幻灯片 | OfficePptSlideTools |
| `office_ppt_delete_slide` | 删除幻灯片 | OfficePptSlideTools |
| `office_ppt_update_slide_title` | 更新幻灯片标题 | OfficePptElementTools |
| `office_ppt_update_slide_body` | 更新幻灯片正文 | OfficePptElementTools |
| `office_ppt_update_slide_notes` | 更新幻灯片备注 | OfficePptElementTools |

## 参数约定

插件框架仅支持 `string` 和 `int` 参数类型：
- 布尔值用 `int`：`0` = false，`1` = true
- 可选字符串：空字符串 `""` 表示未提供
- 数组参数：JSON 数组格式（如 `["A","B"]`）或逗号分隔

## 扩展计划

- V2：Excel（工作簿创建、工作表管理、单元格读写、行操作）
- V3：PowerPoint（幻灯片增删、标题正文替换）
- V4：模板能力、图片插入、复杂表格
