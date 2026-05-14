# 办公文档插件详细方案

## 1. 文档目标

本文档给出一个适配当前 Cortana 插件体系的办公文档插件方案。
目标是让 AI 通过工具调用完成 Word、Excel、PPT 的基础内容操作。
插件本体必须支持 Native AOT 发布。
复杂排版、复杂公式、复杂动画和高保真渲染不纳入第一版目标。

## 2. 目标与非目标

目标范围包括新建文档、读取基础结构、新增内容、修改内容、删除内容、另存为新文件。
Word 重点支持段落、简单表格、占位符替换和图片插入。
Excel 重点支持工作表管理、单元格读写、区域填充和简单公式写入。
PPT 重点支持幻灯片增删、标题正文修改和基于模板追加页面。
非目标包括复杂排版、页码目录精修、审阅修订流、宏、透视表、图表重算和动画编排。
第一版默认不覆盖原文件，所有写操作优先输出到新文件。

## 3. Native AOT 风险评估

当前插件体系已经适合 AOT，工具发现和调用依赖源码生成，不依赖运行时反射注册。
因此插件层应继续保持强类型参数、显式依赖注入和静态工具入口。
必须排除的路线是 Office Interop、VSTO 和基于 COM 的桌面自动化。
这些路线依赖本机安装 Office、线程模型脆弱、运行环境耦合强，而且与 Native AOT 的兼容性差。
推荐主路线是直接操作 docx、xlsx、pptx 文件格式，也就是基于 Open XML 和 OPC 包结构做内容编辑。
如果未来需要高保真导出或复杂版式处理，应通过外部进程隔离，不放进插件本体。

## 4. 总体架构

方案采用两层结构，分别是 AOT 插件层和文档文件处理层。
AOT 插件层负责暴露工具、校验参数、限制访问范围、记录日志和返回结构化结果。
文档文件处理层负责真正的 docx、xlsx、pptx 读写逻辑，不承担 AI 交互职责。
如果以后引入高保真转换服务，则作为第三层外部工作进程，通过本地进程间通信调用。
这样可以保证宿主只加载 AOT 友好的插件，而高风险依赖被隔离在插件进程之外。
第一版只实现前两层，不引入第三层依赖。

## 5. 工具设计原则

每个工具只做一件事，名称和参数要能直接表达意图。
工具应优先使用结构化定位，而不是视觉定位。
允许的定位方式包括段落索引、占位符名称、工作表名称、单元格区域、幻灯片索引和文本匹配。
暂不支持按页面坐标、光标位置或视觉块区域定位内容。
所有写操作都要返回修改摘要，包括目标文件、变更对象、影响数量和输出路径。
删除类工具必须返回删除前匹配结果，必要时要求 AI 先调用读取工具再执行删除。

## 6. 建议公开的工具

Word 建议公开 create_word_document、get_word_outline、insert_word_paragraph、replace_word_text、delete_word_paragraph、insert_word_table、save_word_as。
Excel 建议公开 create_excel_workbook、list_excel_sheets、read_excel_range、write_excel_range、insert_excel_row、delete_excel_row、add_excel_sheet、save_excel_as。
PPT 建议公开 create_ppt_document、list_ppt_slides、add_ppt_slide、update_ppt_slide_title、update_ppt_slide_body、delete_ppt_slide、insert_ppt_image、save_ppt_as。
统一的公共参数应包括 source_path、output_path、document_id、overwrite、template_path、index、name、range 和 content。
第一版不建议做长事务会话，优先保持一次调用对应一次明确的文件操作。
如果后续需要会话能力，可以再引入 open_document 和 close_document，但不作为 MVP 必需项。

## 7. 项目分层建议

建议单独创建一个办公插件项目，例如 Cortana.Plugins.Office。
Startup 只负责注册日志、配置对象、文件系统安全服务和三个文档服务。
OfficeTools 负责暴露工具入口，不直接处理底层 XML 细节。
WordDocumentService、ExcelWorkbookService、PowerPointService 分别处理各自文件类型。
DocumentPathGuard 负责限制操作目录、防止路径穿越和危险覆盖。
OperationResult 和 DocumentSummary 负责统一返回结构，方便 AI 理解结果。

## 8. 底层实现策略

优先使用对 Open XML 友好的实现方式，核心目标是稳定读写文档结构而不是复刻 Office 桌面行为。
Word 部分优先支持段落、Run、表格、书签或占位符文本替换。
Excel 部分优先支持工作表、行列、共享字符串、单元格值和简单公式写入。
PPT 部分优先支持幻灯片列表、标题占位符、正文占位符和图片关系管理。
如果使用第三方库，必须先验证是否存在 RequiresDynamicCode、RequiresUnreferencedCode 或明显的 trimming 警告。
一旦发现库对 Native AOT 兼容性差，应退回到更底层的 zip 包和 XML 处理方案，而不是强行保留高风险依赖。

## 9. 工具调用流程

AI 在执行写操作前，应先调用读取类工具获取结构摘要，例如段落列表、工作表列表或幻灯片列表。
随后 AI 选择明确的结构化目标执行插入、替换或删除，不允许直接提交模糊指令。
写操作完成后，工具返回变更摘要和输出文件路径，供 AI 向用户复述结果。
对于批量修改，建议分多次小范围调用，不建议一个工具承担大量隐式改动。

## 10. 安全与审计

插件只允许访问配置中的工作目录，不允许越权访问任意磁盘路径。
默认另存为新文件，只有显式 overwrite 为 true 时才允许覆盖原文件。
所有操作记录到日志，至少记录时间、工具名、输入文件、输出文件和修改摘要。
删除类工具需要在返回结果中明确列出删除对象编号，便于追踪和回滚。

## 11. 分阶段实施建议

第一阶段只做 Word，完成创建、结构读取、段落插入、文本替换、段落删除和另存为。
第二阶段补 Excel，完成工作簿创建、工作表枚举、区域读取写入和行操作。
第三阶段补 PPT，完成幻灯片增删和标题正文替换。
第四阶段再考虑模板能力、图片插入和简单表格能力。

## 12. 测试与验收

测试重点不是界面行为，而是文件结构正确性、内容变更正确性和保存后的 Office 可打开性。
每个工具都应有最少一条成功用例、一条无效参数用例和一条边界用例。
发布前必须执行 Native AOT 发布验证，确保没有新增 AOT 阻断问题。
结论是该插件完全可以做，但目标必须限定为文档内容层操作，而不是完整 Office 自动化。
