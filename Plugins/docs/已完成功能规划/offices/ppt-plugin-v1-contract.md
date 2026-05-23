# PowerPoint 插件 V1 接口契约

## 1. 文档定位

本文档定义 Cortana.Plugins.Office.PowerPoint 第一版对 AI 公开的工具接口契约。
契约目标是让 AI 可靠完成 PowerPoint 演示文稿的基础创建、结构读取、幻灯片增删、标题正文替换与另存。
本文档是开发、测试、联调和后续版本兼容的统一依据。

## 2. 范围与非范围

V1 支持能力包含演示文稿创建、幻灯片枚举、幻灯片新增、标题占位符替换、正文占位符替换、幻灯片删除、备注编辑和另存为。
V1 支持基于空白或模板创建演示文稿，支持读取每张幻灯片的标题、正文和备注文本。
V1 不包含图片插入与替换、图表、SmartArt、动画、切换效果、母版编辑、主题切换和多媒体嵌入。
V1 不包含精确的字体、颜色、大小等样式控制，仅保留已有样式不主动破坏。
V1 不依赖本机 Office 进程，不使用 COM 或 Interop 自动化。
V1 插件本体必须可 Native AOT 发布，禁止引入明显依赖动态代码生成的实现。

## 3. 版本与命名约定

插件标识建议为 office_ppt，工具名前缀统一为 office_ppt_。
本文档定义的接口版本号为 1.0.0。
新增可选字段不视为破坏性变更。
删除字段、修改字段语义或修改错误码含义视为破坏性变更，必须升级主版本。
工具名称一旦发布不得重命名，废弃能力通过新增替代工具并保留旧工具兼容期实现。

## 4. 公共请求上下文

所有写操作工具必须接收 source_path 和 output_path，但 create_presentation 仅要求 output_path。
source_path 表示输入文件绝对路径或工作目录相对路径。
output_path 表示输出文件路径，默认必须与 source_path 不同。
overwrite 为可选布尔值，默认 false，仅当 output_path 已存在时生效。
operation_id 为可选字符串，用于日志追踪和幂等识别。
slide_index 表示从 0 开始的幻灯片索引。

## 5. 统一返回结构

所有工具返回 JSON 字符串，基础字段为 success、code、message、data。
success 是布尔值，code 是稳定错误码或 OK，message 是面向 AI 的可读说明。
data 包含工具特定结果，写操作必须包含 changed_count 和 output_path。

## 6. 错误码契约

固定错误码包括 INVALID_ARGUMENT、FILE_NOT_FOUND、UNSUPPORTED_FORMAT、PATH_FORBIDDEN、CONFLICT_EXISTS、CONTENT_NOT_FOUND、INTERNAL_ERROR。
当参数缺失、索引越界或文本为空时返回 INVALID_ARGUMENT。
当输入文件不存在时返回 FILE_NOT_FOUND。
当扩展名非 pptx 时返回 UNSUPPORTED_FORMAT。
当路径越权或命中黑名单目录时返回 PATH_FORBIDDEN。
当 overwrite 为 false 且 output_path 已存在时返回 CONFLICT_EXISTS。
当目标幻灯片不存在、占位符未找到或替换文本未匹配时返回 CONTENT_NOT_FOUND。

## 7. 工具契约明细

### 7.1 office_ppt_create_presentation

用途是创建空白 pptx 或基于模板创建新演示文稿。
必填参数为 output_path，可选参数为 template_path、title、author、overwrite。
template_path 存在时按模板复制并保留版式和母版结构，不存在时创建最小可打开演示文稿结构。
空白创建默认包含一张空白版式幻灯片。
成功返回 data.presentation_path、data.slide_count、data.created_from_template、data.output_path。

### 7.2 office_ppt_list_slides

用途是读取演示文稿中的幻灯片摘要供 AI 定位编辑目标。
必填参数为 source_path，可选参数为 include_notes、max_slides。
成功返回 data.slides 列表，每项包含 index、layout_name、title_preview、body_preview、has_notes。
成功返回 data.slide_count。
title_preview 和 body_preview 默认截断到 100 字符。

### 7.3 office_ppt_add_slide

用途是在指定位置插入一张新幻灯片。
必填参数为 source_path、output_path。
可选参数为 insert_index、layout_name、title、body、overwrite。
insert_index 为 -1 或省略时追加到末尾，大于等于 0 时在该索引之前插入。
layout_name 为空字符串时使用默认版式（优先选取标题和内容版式，不存在时选取第一个可用版式）。
title 和 body 为空字符串时不填充对应占位符。
成功返回 data.slide_index、data.layout_name、data.slide_count、data.changed_count、data.output_path。

### 7.4 office_ppt_update_slide_title

用途是替换指定幻灯片的标题占位符文本。
必填参数为 source_path、output_path、slide_index、title。
可选参数为 overwrite。
slide_index 从 0 开始，必须在有效范围内。
当目标幻灯片不存在标题占位符时返回 CONTENT_NOT_FOUND。
替换操作会清除占位符内原有全部文本 Run 并写入新文本，保留占位符外层样式。
成功返回 data.slide_index、data.old_title、data.new_title、data.changed_count、data.output_path。

### 7.5 office_ppt_update_slide_body

用途是替换指定幻灯片的正文占位符文本。
必填参数为 source_path、output_path、slide_index、body。
可选参数为 overwrite。
slide_index 从 0 开始，必须在有效范围内。
body 支持换行符分段：每个 \n 分隔的部分对应一个段落。
当目标幻灯片不存在正文占位符时返回 CONTENT_NOT_FOUND。
替换操作会清除占位符内原有全部段落并写入新内容，保留占位符外层样式。
成功返回 data.slide_index、data.paragraph_count、data.changed_count、data.output_path。

### 7.6 office_ppt_delete_slide

用途是删除指定索引的幻灯片。
必填参数为 source_path、output_path、slide_index。
可选参数为 require_non_empty、overwrite。
slide_index 从 0 开始，必须在有效范围内。
当 require_non_empty 为 true 且目标幻灯片标题和正文均为空时返回 CONTENT_NOT_FOUND。
删除后剩余幻灯片的索引自动重排。
当演示文稿仅剩一张幻灯片时不允许删除，返回 INVALID_ARGUMENT。
成功返回 data.deleted_index、data.slide_count、data.changed_count、data.output_path。

### 7.7 office_ppt_update_slide_notes

用途是设置或替换指定幻灯片的备注文本。
必填参数为 source_path、output_path、slide_index、notes。
可选参数为 overwrite。
slide_index 从 0 开始，必须在有效范围内。
notes 支持换行符分段：每个 \n 分隔的部分对应一个段落。
当幻灯片不存在备注页时自动创建备注页结构。
成功返回 data.slide_index、data.paragraph_count、data.changed_count、data.output_path。

### 7.8 office_ppt_save_as

用途是将 source_path 复制并保存到 output_path，不做内容修改。
必填参数为 source_path、output_path。
可选参数为 overwrite。
成功返回 data.output_path、data.file_size、data.changed_count，changed_count 固定为 0。

## 8. 参数编码约定

插件框架仅支持 string 和 int 参数类型。
布尔值使用 0 和 1 表示。
可选字符串使用空字符串表示未提供。
正文文本中的段落分隔使用 \n 字符。
版式名称按精确匹配处理，大小写敏感。

## 9. 幂等与并发

create_presentation 在 output_path 已存在且 overwrite 为 false 时必须失败。
save_as 在同一路径重复调用且 overwrite 为 false 时必须失败。
update_slide_title 和 update_slide_body 对同一幻灯片重复调用时覆盖前次内容，效果等同于最后一次调用。
add_slide 和 delete_slide 不保证天然幂等，建议调用方通过 operation_id 防重。
同一 source_path 的并发写操作必须串行化处理，避免损坏演示文稿包结构。

## 10. 占位符与版式约束

V1 仅操作两类占位符：标题占位符（PresentationML type="title" 或 type="ctrTitle"）和正文占位符（type="body" 或 type="subTitle"）。
当幻灯片包含多个同类占位符时，优先选取索引最小的占位符进行操作。
V1 不支持按占位符名称或自定义索引定位占位符。
版式解析基于幻灯片与版式的关系链：slide → slideLayout → slideMaster。
如果模板中版式名称使用了本地化语言，AI 应先调用 list_slides 获取 layout_name 后再使用。

## 11. 安全策略

插件仅允许访问配置中的工作目录白名单。
禁止访问系统目录、隐藏目录和宿主明确标记的敏感目录。
输入输出路径在执行前必须完成规范化并再次校验目录边界。
当模板文件来自外部路径时，仍需执行与普通输入文件相同的安全校验。

## 12. 日志与可观测性

每次调用至少记录 time、tool_name、operation_id、source_path、output_path、slide_index、success、code、changed_count。
读取类工具应记录幻灯片数量和结果规模，不记录完整幻灯片正文。
错误日志必须包含异常类型和简化堆栈，但不记录演示文稿完整内容。

## 13. 验收标准

每个工具至少覆盖成功用例、非法参数用例和边界用例。
所有成功输出文件必须可被 PowerPoint 正常打开。
标题和正文替换测试至少覆盖单段落、多段落和空文本场景。
幻灯片增删测试至少覆盖首张、中间和末尾场景。
删除测试必须验证仅剩一张幻灯片时的拒绝行为。
发布前执行 AOT 发布验证，确保无新增阻断告警。

## 14. 实现建议

建议先完成 create_presentation、list_slides、update_slide_title、update_slide_body 四个核心工具形成最小闭环。
随后补 add_slide 和 delete_slide，再补 update_slide_notes 和 save_as。
底层优先直接操作 pptx 的 OPC 包和 PresentationML XML，不依赖 Office 进程。
每完成一个工具即补充对应单元测试和集成测试样例演示文稿。
