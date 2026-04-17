using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Cortana.Plugins.Office;
using Cortana.Plugins.Office.Models;
using Cortana.Plugins.Office.Security;
using Cortana.Plugins.Office.Services;
using Cortana.Plugins.Office.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin.Native.Debugger;

if (args.Length > 0 && string.Equals(args[0], "--test", StringComparison.OrdinalIgnoreCase))
{
    OfficeToolTestRunner.Run();
    return;
}

await PluginDebugRunner.RunAsync(options =>
{
    options.WsPort = 12843;
    options.DataDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Data");
    options.WorkspaceDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Data");
    options.PluginDirectory = Directory.GetCurrentDirectory();
});

static class OfficeToolTestRunner
{
    private const string OkCode = "OK";
    private const string InvalidArgumentCode = "INVALID_ARGUMENT";
    private const string ContentNotFoundCode = "CONTENT_NOT_FOUND";

    public static void Run()
    {
        string outputDir = Path.Combine(AppContext.BaseDirectory, "TestOutput");
        string pptOutputDir = OutputPath(outputDir, "Ppt");
        string wordOutputDir = OutputPath(outputDir, "Word");
        string excelOutputDir = OutputPath(outputDir, "Excel");

        RecreateDirectory(pptOutputDir);
        RecreateDirectory(wordOutputDir);
        RecreateDirectory(excelOutputDir);

        using ServiceProvider provider = BuildServices();
        var presentationTools = provider.GetRequiredService<OfficePptPresentationTools>();
        var slideTools = provider.GetRequiredService<OfficePptSlideTools>();
        var elementTools = provider.GetRequiredService<OfficePptElementTools>();
        var wordDocumentTools = provider.GetRequiredService<OfficeWordDocumentTools>();
        var wordParagraphTools = provider.GetRequiredService<OfficeWordParagraphTools>();
        var wordTableTools = provider.GetRequiredService<OfficeWordTableTools>();
        var excelWorkbookTools = provider.GetRequiredService<OfficeExcelWorkbookTools>();
        var excelRowTools = provider.GetRequiredService<OfficeExcelRowTools>();
        var excelSheetTools = provider.GetRequiredService<OfficeExcelSheetTools>();

        var results = new List<TestCase>();
        PrintHeader(outputDir);

        PrintSuiteHeader("PowerPoint");
        string blankPath = OutputPath(pptOutputDir, "01-create.pptx");
        string templatePath = OutputPath(pptOutputDir, "02-template.pptx");
        string addedPath = OutputPath(pptOutputDir, "03-added-slide.pptx");
        string titlePath = OutputPath(pptOutputDir, "04-title-updated.pptx");
        string bodyPath = OutputPath(pptOutputDir, "05-body-updated.pptx");
        string notesPath = OutputPath(pptOutputDir, "06-notes-updated.pptx");
        string savedPath = OutputPath(pptOutputDir, "07-saved-copy.pptx");
        string deletedPath = OutputPath(pptOutputDir, "08-slide-deleted.pptx");
        string deleteRejectedPath = OutputPath(pptOutputDir, "09-delete-last-slide.pptx");

        RunCreatePresentation(results, presentationTools, blankPath);
        RunCreateFromTemplate(results, presentationTools, blankPath, templatePath);
        RunListSlides(results, presentationTools, blankPath);
        RunAddSlide(results, presentationTools, slideTools, blankPath, addedPath);
        RunUpdateTitle(results, elementTools, addedPath, titlePath);
        RunUpdateBody(results, presentationTools, elementTools, titlePath, bodyPath);
        RunUpdateNotes(results, elementTools, bodyPath, notesPath);
        RunSaveAs(results, presentationTools, notesPath, savedPath);
        RunDeleteSlide(results, presentationTools, slideTools, notesPath, deletedPath);
        RunDeleteLastSlideRejected(results, slideTools, deletedPath, deleteRejectedPath);

        PrintSuiteHeader("Word");
        string wordSeedPath = OutputPath(wordOutputDir, "00-seed.docx");
        string wordBlankPath = OutputPath(wordOutputDir, "01-create.docx");
        string wordTemplatePath = OutputPath(wordOutputDir, "02-template.docx");
        string wordInsertedPath = OutputPath(wordOutputDir, "03-inserted.docx");
        string wordReplacedPath = OutputPath(wordOutputDir, "04-replaced.docx");
        string wordTablePath = OutputPath(wordOutputDir, "05-table.docx");
        string wordDeletedPath = OutputPath(wordOutputDir, "06-deleted.docx");
        string wordSavedPath = OutputPath(wordOutputDir, "07-saved.docx");
        string wordNotFoundPath = OutputPath(wordOutputDir, "08-not-found.docx");

        RunCreateDocument(results, wordDocumentTools, wordBlankPath);
        RunCreateDocumentFromTemplate(results, wordDocumentTools, wordBlankPath, wordTemplatePath);
    CreateSeedWordDocument(wordBlankPath, wordSeedPath, "种子段落");
    RunGetOutline(results, wordDocumentTools, wordSeedPath);
    RunInsertParagraph(results, wordDocumentTools, wordParagraphTools, wordSeedPath, wordInsertedPath);
        RunReplaceText(results, wordDocumentTools, wordParagraphTools, wordInsertedPath, wordReplacedPath);
        RunInsertTable(results, wordDocumentTools, wordTableTools, wordReplacedPath, wordTablePath);
        RunDeleteParagraph(results, wordDocumentTools, wordParagraphTools, wordReplacedPath, wordDeletedPath);
        RunWordSaveAs(results, wordDocumentTools, wordTablePath, wordSavedPath);
        RunReplaceTextNotFound(results, wordParagraphTools, wordDeletedPath, wordNotFoundPath);

        PrintSuiteHeader("Excel");
        string workbookPath = OutputPath(excelOutputDir, "01-create.xlsx");
        string workbookTemplatePath = OutputPath(excelOutputDir, "02-template.xlsx");
        string workbookWrittenPath = OutputPath(excelOutputDir, "03-written.xlsx");
        string workbookInsertedRowPath = OutputPath(excelOutputDir, "04-inserted-row.xlsx");
        string workbookDeletedRowPath = OutputPath(excelOutputDir, "05-deleted-row.xlsx");
        string workbookAddedSheetPath = OutputPath(excelOutputDir, "06-added-sheet.xlsx");
        string workbookSavedPath = OutputPath(excelOutputDir, "07-saved.xlsx");

        RunCreateWorkbook(results, excelWorkbookTools, workbookPath);
        RunCreateWorkbookFromTemplate(results, excelWorkbookTools, workbookPath, workbookTemplatePath);
        RunListSheets(results, excelWorkbookTools, workbookPath);
        RunWriteRange(results, excelWorkbookTools, excelRowTools, workbookPath, workbookWrittenPath);
        RunReadRange(results, excelWorkbookTools, workbookWrittenPath);
        RunInsertRow(results, excelWorkbookTools, excelRowTools, workbookWrittenPath, workbookInsertedRowPath);
        RunDeleteRow(results, excelWorkbookTools, excelRowTools, workbookInsertedRowPath, workbookDeletedRowPath);
        RunAddSheet(results, excelWorkbookTools, excelSheetTools, workbookDeletedRowPath, workbookAddedSheetPath);
        RunExcelSaveAs(results, excelWorkbookTools, workbookAddedSheetPath, workbookSavedPath);

        PrintSummary(results);
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSimpleConsole(options => options.SingleLine = true)
            .SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(new OfficePluginOptions
        {
            AllowedDirectories = []
        });
        services.AddSingleton<IDocumentPathGuard, DocumentPathGuard>();
        services.AddSingleton<IWordDocumentService, WordDocumentService>();
        services.AddSingleton<IExcelWorkbookService, ExcelWorkbookService>();
        services.AddSingleton<IPowerPointService, PowerPointService>();
        services.AddSingleton<OfficeWordDocumentTools>();
        services.AddSingleton<OfficeWordParagraphTools>();
        services.AddSingleton<OfficeWordTableTools>();
        services.AddSingleton<OfficeExcelWorkbookTools>();
        services.AddSingleton<OfficeExcelRowTools>();
        services.AddSingleton<OfficeExcelSheetTools>();
        services.AddSingleton<OfficePptPresentationTools>();
        services.AddSingleton<OfficePptSlideTools>();
        services.AddSingleton<OfficePptElementTools>();
        return services.BuildServiceProvider();
    }

    private static void RunCreatePresentation(List<TestCase> results, OfficePptPresentationTools tools, string outputPath)
    {
        var test = new TestCase("office_ppt_create_presentation", "创建空白演示文稿");
        results.Add(test);

        string json = tools.CreatePresentation(outputPath, "", "测试标题", "测试作者", 1);
        ToolResult toolResult = ParseToolResult(json);
        CreatePresentationResult? data = ParseData<CreatePresentationResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(toolResult.Code == OkCode, $"返回 code={toolResult.Code}");
        test.Expect(data is not null, "data 可反序列化");
        test.Expect(data?.SlideCount == 1, "默认生成 1 张幻灯片");
        test.Expect(data?.CreatedFromTemplate == false, "非模板创建");
        test.Expect(File.Exists(outputPath), "输出文件已生成");
        test.Expect(IsValidPptx(outputPath), "输出文件是有效 pptx 包结构");
        test.Finish($"输出={outputPath}");
    }

    private static void RunCreateFromTemplate(
        List<TestCase> results,
        OfficePptPresentationTools tools,
        string sourcePath,
        string outputPath)
    {
        var test = new TestCase("office_ppt_create_presentation", "基于模板创建演示文稿");
        results.Add(test);

        string json = tools.CreatePresentation(outputPath, sourcePath, "", "", 1);
        ToolResult toolResult = ParseToolResult(json);
        CreatePresentationResult? data = ParseData<CreatePresentationResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.CreatedFromTemplate == true, "标记为模板创建");
        test.Expect(data?.SlideCount == 1, "模板文稿仍为 1 张幻灯片");
        test.Expect(File.Exists(outputPath), "模板输出文件已生成");
        test.Finish($"模板输出={outputPath}");
    }

    private static void RunListSlides(List<TestCase> results, OfficePptPresentationTools tools, string sourcePath)
    {
        var test = new TestCase("office_ppt_list_slides", "读取幻灯片列表");
        results.Add(test);

        string json = tools.ListSlides(sourcePath, 1, 0);
        ToolResult toolResult = ParseToolResult(json);
        ListSlidesResult? data = ParseData<ListSlidesResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.SlideCount == 1, "总幻灯片数为 1");
        test.Expect(data?.Slides.Count == 1, "返回列表长度为 1");
        test.Expect(data?.Slides[0].Index == 0, "首张索引为 0");
        test.Finish($"slide_count={data?.SlideCount}");
    }

    private static void RunAddSlide(
        List<TestCase> results,
        OfficePptPresentationTools presentationTools,
        OfficePptSlideTools slideTools,
        string sourcePath,
        string outputPath)
    {
        var test = new TestCase("office_ppt_add_slide", "追加一张新幻灯片");
        results.Add(test);

        string json = slideTools.AddSlide(sourcePath, outputPath, -1, "", "第二张标题", "第二张正文", 1);
        ToolResult toolResult = ParseToolResult(json);
        AddSlideResult? data = ParseData<AddSlideResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.SlideIndex == 1, "新幻灯片索引为 1");
        test.Expect(data?.SlideCount == 2, "总幻灯片数变为 2");
        test.Expect(File.Exists(outputPath), "输出文件已生成");

        string listJson = presentationTools.ListSlides(outputPath, 0, 0);
        ToolResult listResult = ParseToolResult(listJson);
        ListSlidesResult? listData = ParseData<ListSlidesResult>(listResult);
        test.Expect(listResult.Success, "新增后可再次读取幻灯片列表");
        test.Expect(listData?.SlideCount == 2, "读取结果中的 slide_count 为 2");
        test.Expect(listData?.Slides.Count == 2, "读取结果返回 2 张幻灯片");
        test.Expect(listData?.Slides[1].TitlePreview.Contains("第二张标题") == true, "标题预览包含新增标题");
        test.Expect(listData?.Slides[1].BodyPreview.Contains("第二张正文") == true, "正文预览包含新增正文");
        test.Finish($"新增输出={outputPath}");
    }

    private static void RunUpdateTitle(List<TestCase> results, OfficePptElementTools tools, string sourcePath, string outputPath)
    {
        var test = new TestCase("office_ppt_update_slide_title", "更新幻灯片标题");
        results.Add(test);

        string json = tools.UpdateSlideTitle(sourcePath, outputPath, 1, "修改后的标题", 1);
        ToolResult toolResult = ParseToolResult(json);
        UpdateSlideTitleResult? data = ParseData<UpdateSlideTitleResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.SlideIndex == 1, "目标幻灯片索引为 1");
        test.Expect(data?.NewTitle == "修改后的标题", "新标题字段正确");
        test.Expect(File.Exists(outputPath), "输出文件已生成");
        test.Finish($"输出={outputPath}");
    }

    private static void RunUpdateBody(
        List<TestCase> results,
        OfficePptPresentationTools presentationTools,
        OfficePptElementTools tools,
        string sourcePath,
        string outputPath)
    {
        var test = new TestCase("office_ppt_update_slide_body", "更新幻灯片正文为多段落");
        results.Add(test);

        string json = tools.UpdateSlideBody(sourcePath, outputPath, 1, "第一段\n第二段\n第三段", 1);
        ToolResult toolResult = ParseToolResult(json);
        UpdateSlideBodyResult? data = ParseData<UpdateSlideBodyResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.SlideIndex == 1, "目标幻灯片索引为 1");
        test.Expect(data?.ParagraphCount == 3, "段落数为 3");

        string listJson = presentationTools.ListSlides(outputPath, 0, 0);
        ToolResult listResult = ParseToolResult(listJson);
        ListSlidesResult? listData = ParseData<ListSlidesResult>(listResult);
        test.Expect(listResult.Success, "更新后可读取幻灯片列表");
        test.Expect(listData?.Slides[1].BodyPreview.Contains("第一段") == true, "正文预览包含第一段");
        test.Finish($"输出={outputPath}");
    }

    private static void RunUpdateNotes(List<TestCase> results, OfficePptElementTools tools, string sourcePath, string outputPath)
    {
        var test = new TestCase("office_ppt_update_slide_notes", "更新幻灯片备注");
        results.Add(test);

        string json = tools.UpdateSlideNotes(sourcePath, outputPath, 0, "备注第一段\n备注第二段", 1);
        ToolResult toolResult = ParseToolResult(json);
        UpdateSlideNotesResult? data = ParseData<UpdateSlideNotesResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.SlideIndex == 0, "目标幻灯片索引为 0");
        test.Expect(data?.ParagraphCount == 2, "备注段落数为 2");
        test.Expect(File.Exists(outputPath), "输出文件已生成");
        test.Finish($"输出={outputPath}");
    }

    private static void RunSaveAs(List<TestCase> results, OfficePptPresentationTools tools, string sourcePath, string outputPath)
    {
        var test = new TestCase("office_ppt_save_as", "另存演示文稿");
        results.Add(test);

        string json = tools.SaveAs(sourcePath, outputPath, 1);
        ToolResult toolResult = ParseToolResult(json);
        SaveAsResult? data = ParseData<SaveAsResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.ChangedCount == 0, "changed_count 固定为 0");
        test.Expect(data?.FileSize > 0, "输出文件大小大于 0");
        test.Expect(File.Exists(outputPath), "输出文件已生成");
        test.Finish($"输出={outputPath}");
    }

    private static void RunDeleteSlide(
        List<TestCase> results,
        OfficePptPresentationTools presentationTools,
        OfficePptSlideTools tools,
        string sourcePath,
        string outputPath)
    {
        var test = new TestCase("office_ppt_delete_slide", "删除第二张幻灯片");
        results.Add(test);

        string json = tools.DeleteSlide(sourcePath, outputPath, 1, 0, 1);
        ToolResult toolResult = ParseToolResult(json);
        DeleteSlideResult? data = ParseData<DeleteSlideResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.DeletedIndex == 1, "删除索引为 1");
        test.Expect(data?.SlideCount == 1, "删除后剩余 1 张幻灯片");

        string listJson = presentationTools.ListSlides(outputPath, 0, 0);
        ToolResult listResult = ParseToolResult(listJson);
        ListSlidesResult? listData = ParseData<ListSlidesResult>(listResult);
        test.Expect(listResult.Success, "删除后可读取幻灯片列表");
        test.Expect(listData?.SlideCount == 1, "读取结果中只剩 1 张");
        test.Finish($"输出={outputPath}");
    }

    private static void RunDeleteLastSlideRejected(List<TestCase> results, OfficePptSlideTools tools, string sourcePath, string outputPath)
    {
        var test = new TestCase("office_ppt_delete_slide", "删除最后一张幻灯片应被拒绝");
        results.Add(test);

        string json = tools.DeleteSlide(sourcePath, outputPath, 0, 0, 1);
        ToolResult toolResult = ParseToolResult(json);

        test.Expect(!toolResult.Success, "工具返回 success=false");
        test.Expect(toolResult.Code == InvalidArgumentCode,
            $"按契约应返回 {InvalidArgumentCode}，实际为 {toolResult.Code}");
        test.Finish(toolResult.Message);
    }

    private static void RunCreateDocument(List<TestCase> results, OfficeWordDocumentTools tools, string outputPath)
    {
        var test = new TestCase("office_word_create_document", "创建空白 Word 文档");
        results.Add(test);

        string json = tools.CreateDocument(outputPath, "", "测试文档", "测试作者", 1);
        ToolResult toolResult = ParseToolResult(json);
        CreateDocumentResult? data = ParseData<CreateDocumentResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(toolResult.Code == OkCode, $"返回 code={toolResult.Code}");
        test.Expect(data is not null, "data 可反序列化");
        test.Expect(data?.CreatedFromTemplate == false, "非模板创建");
        test.Expect(File.Exists(outputPath), "输出文件已生成");
        test.Expect(IsValidDocx(outputPath), "输出文件是有效 docx 包结构");
        test.Finish($"输出={outputPath}");
    }

    private static void RunCreateDocumentFromTemplate(
        List<TestCase> results,
        OfficeWordDocumentTools tools,
        string sourcePath,
        string outputPath)
    {
        var test = new TestCase("office_word_create_document", "基于模板创建 Word 文档");
        results.Add(test);

        string json = tools.CreateDocument(outputPath, sourcePath, "", "", 1);
        ToolResult toolResult = ParseToolResult(json);
        CreateDocumentResult? data = ParseData<CreateDocumentResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.CreatedFromTemplate == true, "标记为模板创建");
        test.Expect(File.Exists(outputPath), "模板输出文件已生成");
        test.Expect(IsValidDocx(outputPath), "模板输出文件是有效 docx");
        test.Finish($"模板输出={outputPath}");
    }

    private static void RunGetOutline(List<TestCase> results, OfficeWordDocumentTools tools, string sourcePath)
    {
        var test = new TestCase("office_word_get_outline", "读取 Word 文档大纲");
        results.Add(test);

        string json = tools.GetOutline(sourcePath, 1, 0);
        ToolResult toolResult = ParseToolResult(json);
        DocumentOutlineResult? data = ParseData<DocumentOutlineResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data is not null, "data 可反序列化");
        test.Expect(data?.Paragraphs.Count >= 1, "至少返回 1 个段落");
        test.Expect(data?.Paragraphs.FirstOrDefault()?.Index == 0, "首段索引为 0");
        test.Expect(data?.TablesCount == 0, "初始文档表格数为 0");
        test.Finish($"paragraphs={data?.Paragraphs.Count}, tables={data?.TablesCount}");
    }

    private static void RunInsertParagraph(
        List<TestCase> results,
        OfficeWordDocumentTools documentTools,
        OfficeWordParagraphTools paragraphTools,
        string sourcePath,
        string outputPath)
    {
        var test = new TestCase("office_word_insert_paragraph", "在锚点段落后插入新段落");
        results.Add(test);

        string json = paragraphTools.InsertParagraph(sourcePath, outputPath, 0, "after", "第一段正文", 1);
        ToolResult toolResult = ParseToolResult(json);
        InsertParagraphResult? data = ParseData<InsertParagraphResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.InsertedIndex == 1, "插入索引应为 1");
        test.Expect(File.Exists(outputPath), "输出文件已生成");
        test.Expect(IsValidDocx(outputPath), "输出文件是有效 docx");

        string outlineJson = documentTools.GetOutline(outputPath, 1, 0);
        DocumentOutlineResult? outline = ParseData<DocumentOutlineResult>(ParseToolResult(outlineJson));
        test.Expect(outline?.Paragraphs.Count >= 2, "插入后段落数至少为 2");
        test.Expect(outline?.Paragraphs.Any(p => p.TextPreview.Contains("第一段正文")) == true, "大纲中包含插入文本");
        test.Finish($"输出={outputPath}");
    }

    private static void RunReplaceText(
        List<TestCase> results,
        OfficeWordDocumentTools documentTools,
        OfficeWordParagraphTools paragraphTools,
        string sourcePath,
        string outputPath)
    {
        var test = new TestCase("office_word_replace_text", "替换 Word 文档中的文本");
        results.Add(test);

        string json = paragraphTools.ReplaceText(sourcePath, outputPath, "第一段", "第二段", 0, 1, 0, 1);
        ToolResult toolResult = ParseToolResult(json);
        ReplaceTextResult? data = ParseData<ReplaceTextResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.ReplacedCount == 1, "替换数量为 1");
        test.Expect(File.Exists(outputPath), "输出文件已生成");
        test.Expect(IsValidDocx(outputPath), "输出文件是有效 docx");

        string outlineJson = documentTools.GetOutline(outputPath, 1, 0);
        DocumentOutlineResult? outline = ParseData<DocumentOutlineResult>(ParseToolResult(outlineJson));
        test.Expect(outline?.Paragraphs.Any(p => p.TextPreview.Contains("第二段正文")) == true, "替换后的文本可从大纲读回");
        test.Finish($"输出={outputPath}");
    }

    private static void RunInsertTable(
        List<TestCase> results,
        OfficeWordDocumentTools documentTools,
        OfficeWordTableTools tableTools,
        string sourcePath,
        string outputPath)
    {
        var test = new TestCase("office_word_insert_table", "在 Word 文档中插入表格");
        results.Add(test);

        string json = tableTools.InsertTable(sourcePath, outputPath, 0, 2, 2, "列1,列2", "A1,B1,A2,B2", 1);
        ToolResult toolResult = ParseToolResult(json);
        InsertTableResult? data = ParseData<InsertTableResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.Rows == 2, "表格行数为 2");
        test.Expect(data?.Columns == 2, "表格列数为 2");
        test.Expect(File.Exists(outputPath), "输出文件已生成");
        test.Expect(IsValidDocx(outputPath), "输出文件是有效 docx");

        string outlineJson = documentTools.GetOutline(outputPath, 1, 0);
        DocumentOutlineResult? outline = ParseData<DocumentOutlineResult>(ParseToolResult(outlineJson));
        test.Expect(outline?.TablesCount == 1, "文档中表格数量为 1");
        test.Finish($"输出={outputPath}");
    }

    private static void RunDeleteParagraph(
        List<TestCase> results,
        OfficeWordDocumentTools documentTools,
        OfficeWordParagraphTools paragraphTools,
        string sourcePath,
        string outputPath)
    {
        var test = new TestCase("office_word_delete_paragraph", "删除非空段落");
        results.Add(test);

        string json = paragraphTools.DeleteParagraph(sourcePath, outputPath, 1, 1, 1);
        ToolResult toolResult = ParseToolResult(json);
        DeleteParagraphResult? data = ParseData<DeleteParagraphResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.DeletedIndex == 1, "删除索引为 1");
        test.Expect(File.Exists(outputPath), "输出文件已生成");
        test.Expect(IsValidDocx(outputPath), "输出文件是有效 docx");

        string outlineJson = documentTools.GetOutline(outputPath, 1, 0);
        DocumentOutlineResult? outline = ParseData<DocumentOutlineResult>(ParseToolResult(outlineJson));
        test.Expect(outline?.Paragraphs.Any(p => p.TextPreview.Contains("第二段正文")) == false, "删除后不再包含目标段落文本");
        test.Finish($"输出={outputPath}");
    }

    private static void RunWordSaveAs(List<TestCase> results, OfficeWordDocumentTools tools, string sourcePath, string outputPath)
    {
        var test = new TestCase("office_word_save_as", "另存 Word 文档");
        results.Add(test);

        string json = tools.SaveAs(sourcePath, outputPath, 1);
        ToolResult toolResult = ParseToolResult(json);
        SaveAsResult? data = ParseData<SaveAsResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.ChangedCount == 0, "changed_count 固定为 0");
        test.Expect(data?.FileSize > 0, "输出文件大小大于 0");
        test.Expect(File.Exists(outputPath), "输出文件已生成");
        test.Expect(IsValidDocx(outputPath), "输出文件是有效 docx");
        test.Finish($"输出={outputPath}");
    }

    private static void RunReplaceTextNotFound(
        List<TestCase> results,
        OfficeWordParagraphTools tools,
        string sourcePath,
        string outputPath)
    {
        var test = new TestCase("office_word_replace_text", "替换不存在文本时应返回 CONTENT_NOT_FOUND");
        results.Add(test);

        string json = tools.ReplaceText(sourcePath, outputPath, "不存在的文本", "替换", 0, 1, 0, 1);
        ToolResult toolResult = ParseToolResult(json);

        test.Expect(!toolResult.Success, "工具返回 success=false");
        test.Expect(toolResult.Code == ContentNotFoundCode, $"按契约应返回 {ContentNotFoundCode}，实际为 {toolResult.Code}");
        test.Expect(!File.Exists(outputPath), "未发生替换时不应保留输出文件");
        test.Finish(toolResult.Message);
    }

    private static void RunCreateWorkbook(List<TestCase> results, OfficeExcelWorkbookTools tools, string outputPath)
    {
        var test = new TestCase("office_excel_create_workbook", "创建空白 Excel 工作簿");
        results.Add(test);

        string json = tools.CreateWorkbook(outputPath, "", "Data,Stats", "测试作者", 1);
        ToolResult toolResult = ParseToolResult(json);
        CreateWorkbookResult? data = ParseData<CreateWorkbookResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.SheetCount == 2, "创建 2 个工作表");
        test.Expect(data?.CreatedFromTemplate == false, "非模板创建");
        test.Expect(File.Exists(outputPath), "输出文件已生成");
        test.Expect(IsValidXlsx(outputPath), "输出文件是有效 xlsx 包结构");
        test.Finish($"输出={outputPath}");
    }

    private static void RunCreateWorkbookFromTemplate(
        List<TestCase> results,
        OfficeExcelWorkbookTools tools,
        string sourcePath,
        string outputPath)
    {
        var test = new TestCase("office_excel_create_workbook", "基于模板创建 Excel 工作簿");
        results.Add(test);

        string json = tools.CreateWorkbook(outputPath, sourcePath, "", "", 1);
        ToolResult toolResult = ParseToolResult(json);
        CreateWorkbookResult? data = ParseData<CreateWorkbookResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.CreatedFromTemplate == true, "标记为模板创建");
        test.Expect(data?.SheetCount == 2, "模板工作表数为 2");
        test.Expect(File.Exists(outputPath), "模板输出文件已生成");
        test.Expect(IsValidXlsx(outputPath), "模板输出文件是有效 xlsx");
        test.Finish($"模板输出={outputPath}");
    }

    private static void RunListSheets(List<TestCase> results, OfficeExcelWorkbookTools tools, string sourcePath)
    {
        var test = new TestCase("office_excel_list_sheets", "读取工作表列表");
        results.Add(test);

        string json = tools.ListSheets(sourcePath, 1, 0);
        ToolResult toolResult = ParseToolResult(json);
        ListSheetsResult? data = ParseData<ListSheetsResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.SheetCount == 2, "总工作表数为 2");
        test.Expect(data?.Sheets.Count == 2, "返回 2 个工作表摘要");
        test.Expect(data?.Sheets[0].Name == "Data", "首个工作表名称为 Data");
        test.Expect(data?.Sheets[1].Name == "Stats", "第二个工作表名称为 Stats");
        test.Finish($"active_sheet={data?.ActiveSheet}");
    }

    private static void RunWriteRange(
        List<TestCase> results,
        OfficeExcelWorkbookTools workbookTools,
        OfficeExcelRowTools rowTools,
        string sourcePath,
        string outputPath)
    {
        var test = new TestCase("office_excel_write_range", "写入 Excel 区域数据");
        results.Add(test);

        string json = rowTools.WriteRange(sourcePath, outputPath, "Data", "A1",
            "[[\"Name\",\"Score\"],[\"Alice\",\"95\"],[\"Bob\",\"88\"]]", 1);
        ToolResult toolResult = ParseToolResult(json);
        WriteRangeResult? data = ParseData<WriteRangeResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.ChangedCount == 6, "共写入 6 个单元格");
        test.Expect(data?.EndCell == "B3", "结束单元格为 B3");
        test.Expect(File.Exists(outputPath), "输出文件已生成");
        test.Expect(IsValidXlsx(outputPath), "输出文件是有效 xlsx");

        string readJson = workbookTools.ReadRange(outputPath, "Data", "A1:B3", 0);
        ReadRangeResult? readData = ParseData<ReadRangeResult>(ParseToolResult(readJson));
        test.Expect(readData?.Values[1][0] == "Alice", "写入后可读回 Alice");
        test.Expect(readData?.Values[2][1] == "88", "写入后可读回 Bob 的分数");
        test.Finish($"输出={outputPath}");
    }

    private static void RunReadRange(List<TestCase> results, OfficeExcelWorkbookTools tools, string sourcePath)
    {
        var test = new TestCase("office_excel_read_range", "读取 Excel 区域数据");
        results.Add(test);

        string json = tools.ReadRange(sourcePath, "Data", "A1:B3", 0);
        ToolResult toolResult = ParseToolResult(json);
        ReadRangeResult? data = ParseData<ReadRangeResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.Rows == 3, "行数为 3");
        test.Expect(data?.Columns == 2, "列数为 2");
        test.Expect(data?.Values[0][0] == "Name", "A1 为 Name");
        test.Expect(data?.Values[1][1] == "95", "B2 为 95");
        test.Finish($"range={data?.RangeRef}");
    }

    private static void RunInsertRow(
        List<TestCase> results,
        OfficeExcelWorkbookTools workbookTools,
        OfficeExcelRowTools rowTools,
        string sourcePath,
        string outputPath)
    {
        var test = new TestCase("office_excel_insert_row", "在 Excel 工作表中插入空行");
        results.Add(test);

        string json = rowTools.InsertRow(sourcePath, outputPath, "Data", 2, 1, 1);
        ToolResult toolResult = ParseToolResult(json);
        InsertRowResult? data = ParseData<InsertRowResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.InsertedAt == 2, "插入位置为第 2 行");
        test.Expect(data?.RowCount == 1, "插入 1 行");
        test.Expect(File.Exists(outputPath), "输出文件已生成");
        test.Expect(IsValidXlsx(outputPath), "输出文件是有效 xlsx");

        string readJson = workbookTools.ReadRange(outputPath, "Data", "A1:B4", 0);
        ReadRangeResult? readData = ParseData<ReadRangeResult>(ParseToolResult(readJson));
        test.Expect(string.IsNullOrEmpty(readData?.Values[1][0]), "插入后的第 2 行首单元格为空");
        test.Expect(readData?.Values[2][0] == "Alice", "原数据下移到第 3 行");
        test.Finish($"输出={outputPath}");
    }

    private static void RunDeleteRow(
        List<TestCase> results,
        OfficeExcelWorkbookTools workbookTools,
        OfficeExcelRowTools rowTools,
        string sourcePath,
        string outputPath)
    {
        var test = new TestCase("office_excel_delete_row", "删除 Excel 中的空行");
        results.Add(test);

        string json = rowTools.DeleteRow(sourcePath, outputPath, "Data", 2, 1, 0, 1);
        ToolResult toolResult = ParseToolResult(json);
        DeleteRowResult? data = ParseData<DeleteRowResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.DeletedAt == 2, "删除位置为第 2 行");
        test.Expect(data?.RowCount == 1, "删除 1 行");
        test.Expect(File.Exists(outputPath), "输出文件已生成");
        test.Expect(IsValidXlsx(outputPath), "输出文件是有效 xlsx");

        string readJson = workbookTools.ReadRange(outputPath, "Data", "A1:B3", 0);
        ReadRangeResult? readData = ParseData<ReadRangeResult>(ParseToolResult(readJson));
        test.Expect(readData?.Values[1][0] == "Alice", "删除空行后 Alice 回到第 2 行");
        test.Expect(readData?.Values[2][0] == "Bob", "Bob 位于第 3 行");
        test.Finish($"输出={outputPath}");
    }

    private static void RunAddSheet(
        List<TestCase> results,
        OfficeExcelWorkbookTools workbookTools,
        OfficeExcelSheetTools sheetTools,
        string sourcePath,
        string outputPath)
    {
        var test = new TestCase("office_excel_add_sheet", "新增 Excel 工作表");
        results.Add(test);

        string json = sheetTools.AddSheet(sourcePath, outputPath, "Archive", 0, 1);
        ToolResult toolResult = ParseToolResult(json);
        AddSheetResult? data = ParseData<AddSheetResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.SheetName == "Archive", "新增工作表名称正确");
        test.Expect(data?.SheetCount == 3, "总工作表数变为 3");
        test.Expect(File.Exists(outputPath), "输出文件已生成");
        test.Expect(IsValidXlsx(outputPath), "输出文件是有效 xlsx");

        string listJson = workbookTools.ListSheets(outputPath, 0, 0);
        ListSheetsResult? listData = ParseData<ListSheetsResult>(ParseToolResult(listJson));
        test.Expect(listData?.Sheets.Any(s => s.Name == "Archive") == true, "工作表列表中包含 Archive");
        test.Finish($"输出={outputPath}");
    }

    private static void RunExcelSaveAs(List<TestCase> results, OfficeExcelWorkbookTools tools, string sourcePath, string outputPath)
    {
        var test = new TestCase("office_excel_save_as", "另存 Excel 工作簿");
        results.Add(test);

        string json = tools.SaveAs(sourcePath, outputPath, 1);
        ToolResult toolResult = ParseToolResult(json);
        SaveAsResult? data = ParseData<SaveAsResult>(toolResult);

        test.Expect(toolResult.Success, "工具返回 success=true");
        test.Expect(data?.ChangedCount == 0, "changed_count 固定为 0");
        test.Expect(data?.FileSize > 0, "输出文件大小大于 0");
        test.Expect(File.Exists(outputPath), "输出文件已生成");
        test.Expect(IsValidXlsx(outputPath), "输出文件是有效 xlsx");
        test.Finish($"输出={outputPath}");
    }

    private static ToolResult ParseToolResult(string json)
    {
        return JsonSerializer.Deserialize<ToolResult>(json)
            ?? throw new InvalidOperationException("工具返回结果无法反序列化为 ToolResult。");
    }

    private static T? ParseData<T>(ToolResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Data))
            return default;

        return JsonSerializer.Deserialize<T>(result.Data);
    }

    private static string OutputPath(string directory, string fileName) => Path.Combine(directory, fileName);

    private static void CreateSeedWordDocument(string sourcePath, string outputPath, string text)
    {
        File.Copy(sourcePath, outputPath, true);

        using var stream = new FileStream(outputPath, FileMode.Open, FileAccess.ReadWrite);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Update);

        var entry = archive.GetEntry("word/document.xml")
            ?? throw new InvalidOperationException("找不到 word/document.xml");

        XDocument doc;
        using (var entryStream = entry.Open())
        {
            doc = XDocument.Load(entryStream);
        }

        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var body = doc.Root?.Element(w + "body")
            ?? throw new InvalidOperationException("找不到 Word body 节点。");

        body.Add(new XElement(w + "p",
            new XElement(w + "r",
                new XElement(w + "t", text))));

        entry.Delete();
        var newEntry = archive.CreateEntry("word/document.xml");
        using var writer = new StreamWriter(newEntry.Open());
        doc.Save(writer);
    }

    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, true);
        Directory.CreateDirectory(path);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static bool IsValidPptx(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entryNames = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return entryNames.Contains("[Content_Types].xml")
                && entryNames.Contains("_rels/.rels")
                && entryNames.Contains("ppt/presentation.xml");
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidDocx(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entryNames = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return entryNames.Contains("[Content_Types].xml")
                && entryNames.Contains("_rels/.rels")
                && entryNames.Contains("word/document.xml");
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidXlsx(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entryNames = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return entryNames.Contains("[Content_Types].xml")
                && entryNames.Contains("_rels/.rels")
                && entryNames.Contains("xl/workbook.xml");
        }
        catch
        {
            return false;
        }
    }

    private static void PrintHeader(string outputDir)
    {
        Console.WriteLine("Office 工具测试");
        Console.WriteLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"输出目录: {outputDir}");
        Console.WriteLine(new string('=', 72));
    }

    private static void PrintSuiteHeader(string suiteName)
    {
        Console.WriteLine();
        Console.WriteLine($"[{suiteName}] 开始执行");
        Console.WriteLine(new string('-', 72));
    }

    private static void PrintSummary(List<TestCase> results)
    {
        Console.WriteLine(new string('=', 72));

        foreach (var result in results)
        {
            string status = result.Passed ? "PASS" : "FAIL";
            Console.WriteLine($"[{status}] {result.Name} - {result.Description}");

            foreach (string line in result.Details)
                Console.WriteLine($"  - {line}");

            if (!string.IsNullOrWhiteSpace(result.Note))
                Console.WriteLine($"  => {result.Note}");
        }

        int passed = results.Count(result => result.Passed);
        int failed = results.Count - passed;
        Console.WriteLine(new string('=', 72));
        Console.WriteLine($"汇总: total={results.Count}, passed={passed}, failed={failed}");
    }
}

sealed class TestCase(string name, string description)
{
    private readonly List<string> details = [];
    private bool allPassed = true;

    public string Name { get; } = name;
    public string Description { get; } = description;
    public IReadOnlyList<string> Details => details;
    public bool Passed { get; private set; }
    public string? Note { get; private set; }

    public void Expect(bool condition, string description)
    {
        allPassed &= condition;
        details.Add($"[{(condition ? "OK" : "NG")}] {description}");
    }

    public void Finish(string note)
    {
        Passed = allPassed;
        Note = note;
    }
}