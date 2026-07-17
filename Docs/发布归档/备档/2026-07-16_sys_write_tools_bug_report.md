# sys_write 系列工具写入大文档故障报告

## 修复状态

**已修复（2026-07-17）**

`FileBrowserToolJsonContext.cs` 补注 `[JsonSerializable(typeof(bool?))]`，AOT 环境下 `Nullable<bool>` 反序列化恢复正常。受影响的四个工具（`sys_write_file`、`sys_write_large_file`、`sys_edit_file`、`sys_write_files_batch`）均通过同一 `serializerOptions: FileBrowserToolJsonContext.Default.Options` 统一修复，无需逐工具单独处理。

---

## 问题概述

在写入一份约 300 行的 Markdown 规格文档时，`sys_write_large_file` 和 `sys_write_file` 在传入 `backup`/`overwrite` 布尔参数时均触发 JSON 反序列化异常，导致写入失败。最终只能通过 `sys_create_file` + `sys_edit_file` 分块 insert 的方式绕行完成。

---

## 工具参数定义参考

### sys_write_file
```json
{
  "path": "string | null",
  "content": "string | null",
  "backup": "boolean | null (default: true)"
}
```

### sys_write_large_file
```json
{
  "path": "string | null",
  "content": "string | null",
  "overwrite": "boolean | null",
  "backup": "boolean | null"
}
```

两个工具的 `backup` 和 `overwrite` 类型均为 `System.Nullable<bool>`。

---

## 失败调用记录

### 失败 #1：sys_write_large_file

**传入参数：**

```json
{
  "path": "临时文件/导出中/2026-07-16_shopify_sx-spy_missing_tools_spec.md",
  "content": "# sx-spy Shopify Plugin Missing Tools Spec ... (约 300 行完整内容)",
  "overwrite": true,
  "backup": false
}
```

**返回错误：**

```
[Exception] The JSON value could not be converted to System.Nullable`1[System.Boolean].
Path: $ | LineNumber: 0 | BytePositionInLine: 6.
```

---

### 失败 #2：sys_write_large_file（第二次尝试）

**传入参数：**

```json
{
  "path": "临时文件/导出中/2026-07-16_shopify_sx-spy_missing_tools_spec.md",
  "content": "# sx-spy Shopify Plugin Missing Tools Spec ... (约 300 行完整内容)",
  "overwrite": true,
  "backup": false
}
```

**返回错误：** 同上

```
[Exception] The JSON value could not be converted to System.Nullable`1[System.Boolean].
Path: $ | LineNumber: 0 | BytePositionInLine: 6.
```

---

### 失败 #3：sys_write_file

**传入参数：**

```json
{
  "path": "临时文件/导出中/2026-07-16_shopify_sx-spy_missing_tools_spec.md",
  "content": "# sx-spy Shopify 插件缺失工具补充规格 ... (约 30 行)",
  "backup": false
}
```

**返回错误：**

```
[Exception] The JSON value could not be converted to System.Nullable`1[System.Boolean].
Path: $ | LineNumber: 0 | BytePositionInLine: 7.
```

---

### 失败 #4：sys_write_file（第二次尝试）

**传入参数：** 同上

**返回错误：** 同上

```
[Exception] The JSON value could not be converted to System.Nullable`1[System.Boolean].
Path: $ | LineNumber: 0 | BytePositionInLine: 7.
```

---

### 失败 #5：sys_edit_file insert（含 expectedHash）

**传入参数：**

```json
{
  "path": "临时文件/导出中/2026-07-16_shopify_sx-spy_missing_tools_spec.md",
  "operation": "insert",
  "startLine": 65,
  "content": "... 主题管理模块内容 ...",
  "expectedHash": "B4A9288038FCD1ADCCCCE0A245DFA8DD701D5BCF2DB946252CC2E765F0D4A291",
  "backup": true
}
```

**返回错误：**

```
[Exception] The JSON value could not be converted to System.Nullable`1[System.Boolean].
Path: $ | LineNumber: 0 | BytePositionInLine: 6.
```

---

### 失败 #6：sys_edit_file insert（不含 expectedHash，不含 backup）

**传入参数：**

```json
{
  "path": "临时文件/导出中/2026-07-16_shopify_sx-spy_missing_tools_spec.md",
  "operation": "insert",
  "startLine": 65,
  "content": "... 主题管理模块内容 ..."
}
```

**返回错误：**

```
[Exception] The JSON value could not be converted to System.Nullable`1[System.Boolean].
Path: $ | LineNumber: 0 | BytePositionInLine: 6.
```

---

## 成功调用记录

### 成功 #1：sys_create_file（无 backup/overwrite 参数）

```json
{
  "path": "临时文件/导出中/2026-07-16_sx-spy_missing_tools.md",
  "content": "# sx-spy Shopify 插件缺失工具补充规格 ... (约 32 行)"
}
```

**结果：** 成功，返回文件创建确认。

### 成功 #2 ~ #6：sys_edit_file insert（分块追加，不传 backup/overwrite）

```json
{
  "path": "临时文件/导出中/2026-07-16_sx-spy_missing_tools.md",
  "operation": "insert",
  "startLine": 33,
  "content": "--- 主题管理模块 --- ... (约 37 行)"
}
```

**结果：** 全部成功，共追加 5 次完成全文。

### 成功 #7：sys_write_file（不传 backup 参数）

早期创建第一版文档时，只传 path 和 content，未传 backup：

```json
{
  "path": "临时文件/导出中/2026-07-16_shopify_plugin_product_upload_capability_proposal.md",
  "content": "... (约 100 行)"
}
```

**结果：** 成功。

---

## 根因分析

### 核心问题

所有传入 `backup` 或 `overwrite` 参数（无论值为 `true` 还是 `false`）的调用均失败。错误信息统一为：

```
The JSON value could not be converted to System.Nullable`1[System.Boolean].
```

**不传** `backup` / `overwrite` 时，调用正常。

### 推测原因

工具端的参数反序列化逻辑在处理 `Nullable<bool>` 类型时存在缺陷：

1. **可能的原因 A**：反序列化器将 JSON 中的 `true`/`false` 解析为字符串 `"true"`/`"false"`，然后尝试将字符串转为 `bool?` 失败
2. **可能的原因 B**：参数 schema 中 `backup`/`overwrite` 声明为 `boolean`，但反序列化器配置了严格模式，不接受非字符串的布尔值
3. **可能的原因 C**：中间层在构建参数 JSON 时，对 `boolean` 类型做了额外包装或类型转换

### BytePositionInLine 差异

- `sys_write_large_file`：BytePositionInLine: 6
- `sys_write_file`：BytePositionInLine: 7
- `sys_edit_file`：BytePositionInLine: 6

偏移量差异可能是因为 JSON 序列化时参数名长度不同，导致错误位置不同，但根因相同。

---

## 绕行方案

当前可行的 workaround：

1. 创建新文件：用 `sys_create_file`，不传 `backup` 参数
2. 追加内容：用 `sys_edit_file` insert，不传 `backup` 和 `expectedHash`
3. 覆盖文件：用 `sys_write_file`，不传 `backup`（仅限文件不存在时等同于创建，存在时等同于覆盖）

**局限性：**

- 无法在写入时指定 `backup: false`（始终走默认备份）
- 大文件只能分块追加，效率低
- `sys_edit_file` insert 在多次调用时性能下降明显

---

## 修复建议

1. **检查 `backup` 和 `overwrite` 参数的反序列化路径**，确认 JSON `boolean` 值能否正确映射到 `System.Nullable<bool>`
2. **添加单元测试**，覆盖以下输入组合：
   - `backup: true`
   - `backup: false`
   - `backup` 不传
   - `overwrite: true`
   - `overwrite: false`
   - `overwrite` 不传
3. **受影响工具清单**：
   - `sys_write_file`（backup 参数）
   - `sys_write_large_file`（backup + overwrite 参数）
   - `sys_edit_file`（backup 参数，当 operation=insert 时也受影响）
   - `sys_write_files_batch`（files 内每个 item 的 overwrite 参数，需同步检查）
