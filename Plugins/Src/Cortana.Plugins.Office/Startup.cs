using Cortana.Plugins.Office.Security;
using Cortana.Plugins.Office.Services;
using Microsoft.Extensions.DependencyInjection;
using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Office;

/// <summary>
/// 办公文档插件入口，负责向 DI 容器注册所有依赖。
/// </summary>
[Plugin(
    Id = "office",
    Name = "办公文档插件",
    Version = "1.0.1",
    Description = "提供 Word / Excel / PowerPoint 文档的创建、结构读取、内容编辑和另存操作。",
    Tags = ["办公", "Word", "Excel", "PowerPoint", "文档", "演示文稿", "docx", "xlsx", "pptx"],
    Instructions = """
        使用 office_word_ 开头的工具操作 Word 文档，使用 office_excel_ 开头的工具操作 Excel 工作簿，使用 office_ppt_ 开头的工具操作 PowerPoint 演示文稿。
        操作 Word 前应先调用 office_word_get_outline 获取文档结构以确定目标段落索引。
        操作 Excel 前应先调用 office_excel_list_sheets 获取工作表列表以确定目标工作表。
        操作 PowerPoint 前应先调用 office_ppt_list_slides 获取幻灯片列表以确定目标幻灯片。
        读写 Excel 区域时使用 A1 表示法（如 A1:C5）。
        所有写操作默认输出到新文件，不覆盖原文件。
        布尔参数使用 0/1 表示，数组参数使用 JSON 数组格式或逗号分隔。
        """)]
public static partial class Startup
{
    /// <summary>
    /// 向插件容器注册办公插件使用的服务。
    /// </summary>
    public static void Configure(IServiceCollection services)
    {
        services.AddLogging();

        // 插件配置：生产环境应设置 AllowedDirectories 白名单
        services.AddSingleton(new OfficePluginOptions
        {
            AllowedDirectories = []
        });

        // 安全层：文档路径守卫
        services.AddSingleton<IDocumentPathGuard, DocumentPathGuard>();

        // 服务层：Word 文档操作
        services.AddSingleton<IWordDocumentService, WordDocumentService>();

        // 服务层：Excel 工作簿操作
        services.AddSingleton<IExcelWorkbookService, ExcelWorkbookService>();

        // 服务层：PowerPoint 演示文稿操作
        services.AddSingleton<IPowerPointService, PowerPointService>();
    }
}
