# Word 插件 V1 接口契约

## 1. 文档定位

本文档定义 Cortana.Plugins.Office.Word 第一版对 AI 公开的工具接口契约。
契约目标是让 AI 可靠完成 Word 文档的基础新增、修改、删除与另存操作。
本文档是开发、测试、联调和后续版本兼容的统一依据。

## 2. 范围与非范围

V1 支持能力包含文档创建、结构读取、段落插入、文本替换、段落删除、简单表格插入和另存为。
V1 不包含复杂排版、样式精修、目录页码维护、审阅修订流、宏和脚注尾注高级编辑。
V1 不依赖本机 Office 进程，不使用 COM 或 Interop 自动化。
V1 插件本体必须可 Native AOT 发布，禁止引入明显依赖动态代码生成的实现。

## 3. 版本与命名约定

插件标识建议为 office_word，工具名前缀统一为 office_word_。
本文档定义的接口版本号为 1.0.0。
新增可选字段不视为破坏性变更。
删除字段、修改字段语义或修改错误码含义视为破坏性变更，必须升级主版本。
工具名称一旦发布不得重命名，废弃能力通过新增替代工具并保留旧工具兼容期实现。

## 4. 公共请求上下文

所有写操作工具必须接收 source_path 和 output_path。
source_path 表示输入文件绝对路径或工作目录相对路径。
output_path 表示输出文件路径，默认必须与 source_path 不同。
overwrite 为可选布尔值，默认 false，仅当 output_path 已存在时生效。
operation_id 为可选字符串，用于日志追踪和幂等识别。

## 5. 统一返回结构

所有工具返回 JSON 字符串，基础字段为 success、code、message、data。
success 是布尔值，code 是稳定错误码或 OK，message 是面向 AI 的可读说明。
data 包含工具特定结果，写操作必须包含 changed_count 和 output_path。

## 6. 错误码契约

固定错误码包括 INVALID_ARGUMENT、FILE_NOT_FOUND、UNSUPPORTED_FORMAT、PATH_FORBIDDEN、CONFLICT_EXISTS、CONTENT_NOT_FOUND、INTERNAL_ERROR。
当参数缺失、越界或格式非法时返回 INVALID_ARGUMENT。
当输入文件不存在时返回 FILE_NOT_FOUND。
当扩展名非 docx 时返回 UNSUPPORTED_FORMAT。
当路径越权或命中黑名单目录时返回 PATH_FORBIDDEN。
当 overwrite 为 false 且 output_path 已存在时返回 CONFLICT_EXISTS。
当删除或替换目标未匹配到任何内容时返回 CONTENT_NOT_FOUND。

## 7. 工具契约明细

### 7.1 office_word_create_document

用途是创建空白 docx 或基于模板创建新文档。
必填参数为 output_path，可选参数为 template_path、title、author、overwrite。
template_path 存在时按模板复制并更新元信息，不存在时创建最小可打开文档结构。
成功返回 data.document_path、data.created_from_template、data.output_path。

### 7.2 office_word_get_outline

用途是读取文档结构摘要供 AI 定位编辑目标。
必填参数为 source_path，可选参数 include_paragraph_text、max_paragraphs。
成功返回 data.paragraphs 列表，每项包含 index、style、text_preview、is_empty。
成功返回 data.tables_count 和 data.images_count。

### 7.3 office_word_insert_paragraph

用途是在指定段落前后插入新段落。
必填参数为 source_path、output_path、anchor_index、position、text。
position 仅允许 before 或 after。
anchor_index 必须落在现有段落索引范围内。
成功返回 data.inserted_index、data.changed_count、data.output_path。

### 7.4 office_word_replace_text

用途是按文本匹配执行替换。
必填参数为 source_path、output_path、search_text、replace_text。
可选参数为 match_case、replace_all、max_replace_count。
当 replace_all 为 false 时只替换首个命中。
成功返回 data.replaced_count、data.sample_before、data.sample_after、data.output_path。

### 7.5 office_word_delete_paragraph

用途是删除指定段落。
必填参数为 source_path、output_path、paragraph_index。
可选参数 require_non_empty，默认 false。
当 require_non_empty 为 true 且目标段落为空时，返回 CONTENT_NOT_FOUND。
成功返回 data.deleted_index、data.changed_count、data.output_path。

### 7.6 office_word_insert_table

用途是在指定段落后插入简单表格。
必填参数为 source_path、output_path、anchor_index、rows、columns。
可选参数为 headers、cells，cells 按行优先展开。
rows 和 columns 最小为 1，最大建议为 100。
成功返回 data.table_index、data.rows、data.columns、data.output_path。

### 7.7 office_word_save_as

用途是将 source_path 复制并保存到 output_path，不做内容修改。
必填参数为 source_path、output_path。
可选参数为 overwrite。
成功返回 data.output_path、data.file_size、data.changed_count，changed_count 固定为 0。

## 8. 幂等与并发

create_document 在 output_path 已存在且 overwrite 为 false 时必须失败。
save_as 在同一路径重复调用且 overwrite 为 false 时必须失败。
编辑类工具不保证天然幂等，建议调用方通过 operation_id 防重。
同一 source_path 的并发写操作必须串行化处理，避免损坏文档包结构。

## 9. 安全策略

插件仅允许访问配置中的工作目录白名单。
禁止访问系统目录、隐藏目录和宿主明确标记的敏感目录。
输入输出路径在执行前必须完成规范化并再次校验目录边界。

## 10. 日志与可观测性

每次调用至少记录 time、tool_name、operation_id、source_path、output_path、success、code、changed_count。
错误日志必须包含异常类型和简化堆栈，但不记录文档完整正文。

## 11. 验收标准

每个工具至少覆盖成功用例、非法参数用例和边界用例。
所有成功输出文件必须可被 Word 正常打开。
发布前执行 AOT 发布验证，确保无新增阻断告警。

## 12. 实现建议

建议先完成 create_document、get_outline、replace_text 三个核心工具形成最小闭环。
随后补 insert_paragraph 和 delete_paragraph，再补 insert_table 与 save_as。
每完成一个工具即补充对应单元测试和集成测试样例文档。
