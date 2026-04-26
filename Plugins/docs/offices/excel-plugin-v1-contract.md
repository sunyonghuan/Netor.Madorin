# Excel 插件 V1 接口契约

## 1. 文档定位

本文档定义 Cortana.Plugins.Office.Excel 第一版对 AI 公开的工具接口契约。
契约目标是让 AI 可靠完成 Excel 工作簿的基础创建、读取、写入、行操作与另存。
本文档是开发、测试、联调和后续版本兼容的统一依据。

## 2. 范围与非范围

V1 支持能力包含工作簿创建、工作表枚举、区域读取、区域写入、行插入、行删除、工作表新增和另存为。
V1 支持写入纯文本、数字、布尔值和以 = 开头的简单公式字符串。
V1 不包含图表、透视表、筛选视图、条件格式、批注、图片、合并单元格策略控制和复杂样式编辑。
V1 不依赖本机 Office 进程，不使用 COM 或 Interop 自动化。
V1 插件本体必须可 Native AOT 发布，禁止引入明显依赖动态代码生成的实现。

## 3. 版本与命名约定

插件标识建议为 office_excel，工具名前缀统一为 office_excel_。
本文档定义的接口版本号为 1.0.0。
新增可选字段不视为破坏性变更。
删除字段、修改字段语义或修改错误码含义视为破坏性变更，必须升级主版本。
工具名称一旦发布不得重命名，废弃能力通过新增替代工具并保留旧工具兼容期实现。

## 4. 公共请求上下文

所有写操作工具必须接收 source_path 和 output_path，但 create_workbook 仅要求 output_path。
source_path 表示输入文件绝对路径或工作目录相对路径。
output_path 表示输出文件路径，默认必须与 source_path 不同。
overwrite 为可选布尔值，默认 false，仅当 output_path 已存在时生效。
operation_id 为可选字符串，用于日志追踪和幂等识别。
sheet_name 表示工作表名称，默认按精确匹配处理。
单元格与区域统一使用 A1 表示法，例如 A1、B2:D8。

## 5. 统一返回结构

所有工具返回 JSON 字符串，基础字段为 success、code、message、data。
success 是布尔值，code 是稳定错误码或 OK，message 是面向 AI 的可读说明。
data 包含工具特定结果，写操作必须包含 changed_count 和 output_path。

## 6. 错误码契约

固定错误码包括 INVALID_ARGUMENT、FILE_NOT_FOUND、UNSUPPORTED_FORMAT、PATH_FORBIDDEN、CONFLICT_EXISTS、CONTENT_NOT_FOUND、INTERNAL_ERROR。
当参数缺失、越界、区域格式非法或 JSON 结构不合法时返回 INVALID_ARGUMENT。
当输入文件不存在时返回 FILE_NOT_FOUND。
当扩展名非 xlsx 时返回 UNSUPPORTED_FORMAT。
当路径越权或命中黑名单目录时返回 PATH_FORBIDDEN。
当 overwrite 为 false 且 output_path 已存在时返回 CONFLICT_EXISTS。
当工作表不存在、目标区域无内容或删除目标不存在时返回 CONTENT_NOT_FOUND。

## 7. 工具契约明细

### 7.1 office_excel_create_workbook

用途是创建空白 xlsx 或基于模板创建新工作簿。
必填参数为 output_path，可选参数为 template_path、sheet_names、author、overwrite。
sheet_names 为 JSON 数组字符串或逗号分隔字符串，未提供时默认创建 Sheet1。
template_path 存在时按模板复制并保留工作簿基础结构，不存在时创建最小可打开工作簿结构。
成功返回 data.workbook_path、data.sheet_count、data.created_from_template、data.output_path。

### 7.2 office_excel_list_sheets

用途是读取工作簿中的工作表摘要供 AI 定位编辑目标。
必填参数为 source_path，可选参数为 include_dimensions、max_sheets。
成功返回 data.sheets 列表，每项包含 index、name、state、used_range、row_count、column_count。
成功返回 data.active_sheet 和 data.sheet_count。

### 7.3 office_excel_read_range

用途是读取指定工作表中的单元格区域。
必填参数为 source_path、sheet_name、range_ref。
可选参数为 include_empty、include_formula。
range_ref 必须符合 A1 表示法，且仅允许单个连续矩形区域。
成功返回 data.sheet_name、data.range_ref、data.rows、data.columns、data.values。
当 include_formula 为 true 时，data.values 中含公式的单元格返回原始公式字符串，普通单元格返回值文本。

### 7.4 office_excel_write_range

用途是从指定起始单元格开始写入二维数据块。
必填参数为 source_path、output_path、sheet_name、start_cell、values。
可选参数为 overwrite_cells、trim_trailing_empty_rows。
values 为 JSON 二维数组字符串，外层表示行，内层表示列。
单元格值允许为字符串、数字、布尔值或 null；当字符串以 = 开头时按公式写入。
overwrite_cells 默认为 true；当为 false 且目标区域存在非空单元格时，返回 CONFLICT_EXISTS。
成功返回 data.sheet_name、data.start_cell、data.end_cell、data.changed_count、data.output_path。

### 7.5 office_excel_insert_row

用途是在指定行之前插入一个或多个空行。
必填参数为 source_path、output_path、sheet_name、row_index。
可选参数为 row_count、copy_style_from_above、overwrite。
row_index 采用从 1 开始的行号。
row_count 默认 1，最大建议为 1000。
copy_style_from_above 为 true 时，仅复制上一行的基础样式引用，不保证复制公式与数据验证。
成功返回 data.sheet_name、data.inserted_at、data.row_count、data.changed_count、data.output_path。

### 7.6 office_excel_delete_row

用途是删除指定工作表中的一个或多个连续行。
必填参数为 source_path、output_path、sheet_name、row_index。
可选参数为 row_count、require_non_empty、overwrite。
row_index 采用从 1 开始的行号。
row_count 默认 1，最大建议为 1000。
当 require_non_empty 为 true 且目标范围全部为空行时返回 CONTENT_NOT_FOUND。
成功返回 data.sheet_name、data.deleted_at、data.row_count、data.changed_count、data.output_path。

### 7.7 office_excel_add_sheet

用途是在工作簿中新增工作表。
必填参数为 source_path、output_path、sheet_name。
可选参数为 position_index、overwrite。
sheet_name 在同一工作簿内必须唯一，长度和字符限制必须符合 Excel 命名规则。
position_index 为 0 表示追加到末尾，大于 0 时表示插入到指定顺序位置之前。
成功返回 data.sheet_name、data.position_index、data.sheet_count、data.changed_count、data.output_path。

### 7.8 office_excel_save_as

用途是将 source_path 复制并保存到 output_path，不做内容修改。
必填参数为 source_path、output_path。
可选参数为 overwrite。
成功返回 data.output_path、data.file_size、data.changed_count，changed_count 固定为 0。

## 8. 参数编码约定

插件框架仅支持 string 和 int 参数类型。
布尔值使用 0 和 1 表示。
可选字符串使用空字符串表示未提供。
数组和二维数组使用 JSON 字符串传递，不建议使用 CSV 模拟二维数据。
工作表名称、单元格地址和区域地址默认区分大小写地原样回显，但地址解析按 Excel 规则不区分大小写。

## 9. 幂等与并发

create_workbook 在 output_path 已存在且 overwrite 为 false 时必须失败。
save_as 在同一路径重复调用且 overwrite 为 false 时必须失败。
add_sheet 在目标名称已存在时必须失败，不自动重命名。
write_range、insert_row、delete_row 不保证天然幂等，建议调用方通过 operation_id 防重。
同一 source_path 的并发写操作必须串行化处理，避免损坏工作簿包结构。

## 10. 数据与格式约束

V1 仅要求保证 xlsx 文件结构正确和单元格内容正确，不承诺复杂样式的完全保留。
共享字符串、内联字符串和数值单元格应由实现层统一封装，AI 不直接感知底层存储差异。
公式写入后不要求插件内即时重算，允许由 Excel 客户端在打开文件时完成重算。
read_range 对空单元格默认返回空字符串或 null，具体返回形式一旦确定后必须保持稳定。

## 11. 安全策略

插件仅允许访问配置中的工作目录白名单。
禁止访问系统目录、隐藏目录和宿主明确标记的敏感目录。
输入输出路径在执行前必须完成规范化并再次校验目录边界。
当模板文件来自外部路径时，仍需执行与普通输入文件相同的安全校验。

## 12. 日志与可观测性

每次调用至少记录 time、tool_name、operation_id、source_path、output_path、sheet_name、success、code、changed_count。
读取类工具应记录目标区域和结果规模，不记录完整大面积单元格内容。
错误日志必须包含异常类型和简化堆栈，但不记录整表正文。

## 13. 验收标准

每个工具至少覆盖成功用例、非法参数用例和边界用例。
所有成功输出文件必须可被 Excel 正常打开。
区域写入测试至少覆盖文本、数字、布尔值、空值和公式字符串。
行插入与删除测试至少覆盖首行、中间行和尾部行场景。
发布前执行 AOT 发布验证，确保无新增阻断告警。

## 14. 实现建议

建议先完成 create_workbook、list_sheets、read_range、write_range 四个核心工具形成最小闭环。
随后补 add_sheet，再补 insert_row 与 delete_row，最后补 save_as。
底层优先直接操作 xlsx 的 OPC 包和工作表 XML，不依赖 Office 进程。
每完成一个工具即补充对应单元测试和集成测试样例工作簿。