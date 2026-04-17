using System.Text.Json.Serialization;
using Cortana.Plugins.Office.Models;

namespace Cortana.Plugins.Office;

/// <summary>
/// 插件 JSON 源码生成上下文。
/// 所有需要序列化/反序列化的类型必须在此显式注册，确保 Native AOT 兼容（无运行时反射）。
/// </summary>
// ── 公共类型 ──
[JsonSerializable(typeof(ToolResult))]
[JsonSerializable(typeof(SaveAsResult))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(string[][]))]
// ── Word 类型 ──
[JsonSerializable(typeof(ParagraphInfo))]
[JsonSerializable(typeof(List<ParagraphInfo>))]
[JsonSerializable(typeof(CreateDocumentResult))]
[JsonSerializable(typeof(DocumentOutlineResult))]
[JsonSerializable(typeof(InsertParagraphResult))]
[JsonSerializable(typeof(DeleteParagraphResult))]
[JsonSerializable(typeof(ReplaceTextResult))]
[JsonSerializable(typeof(InsertTableResult))]
// ── Excel 类型 ──
[JsonSerializable(typeof(SheetInfo))]
[JsonSerializable(typeof(List<SheetInfo>))]
[JsonSerializable(typeof(CreateWorkbookResult))]
[JsonSerializable(typeof(ListSheetsResult))]
[JsonSerializable(typeof(ReadRangeResult))]
[JsonSerializable(typeof(WriteRangeResult))]
[JsonSerializable(typeof(InsertRowResult))]
[JsonSerializable(typeof(DeleteRowResult))]
[JsonSerializable(typeof(AddSheetResult))]
// ── PowerPoint 类型 ──
[JsonSerializable(typeof(SlideInfo))]
[JsonSerializable(typeof(List<SlideInfo>))]
[JsonSerializable(typeof(CreatePresentationResult))]
[JsonSerializable(typeof(ListSlidesResult))]
[JsonSerializable(typeof(AddSlideResult))]
[JsonSerializable(typeof(UpdateSlideTitleResult))]
[JsonSerializable(typeof(UpdateSlideBodyResult))]
[JsonSerializable(typeof(DeleteSlideResult))]
[JsonSerializable(typeof(UpdateSlideNotesResult))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class PluginJsonContext : JsonSerializerContext;
