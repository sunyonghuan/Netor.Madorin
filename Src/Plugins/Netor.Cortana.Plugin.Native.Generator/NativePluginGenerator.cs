using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Netor.Cortana.Plugin.Native.Generator.Analysis;
using Netor.Cortana.Plugin.Native.Generator.Emitters;

using System.Collections.Immutable;
using System.Text;

namespace Netor.Cortana.Plugin.Native.Generator;

/// <summary>
/// Native 插件框架的 Incremental Source Generator 入口。
/// <para>
/// 扫描标记了 [Plugin] 的 static partial class 和所有 [Tool] 类，
/// 自动生成 Startup.g.cs（路由 + 桥接 + 5 个导出函数）、
/// PluginJsonContext.g.cs，并通过 AddSource 输出 plugin.json。
/// </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class NativePluginGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 筛选候选类：有 attribute 的 class 声明
        var classDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsCandidateClass(node),
                transform: static (ctx, _) => (ClassDeclarationSyntax)ctx.Node)
            .Collect();

        // 组合 Compilation + 候选类
        var combined = context.CompilationProvider
            .Combine(classDeclarations);

        context.RegisterSourceOutput(combined, static (spc, source) =>
        {
            var (compilation, candidates) = source;
            Execute(compilation, candidates, spc);
        });
    }

    /// <summary>
    /// 快速语法过滤：只关心有 Attribute 的 class 声明。
    /// </summary>
    private static bool IsCandidateClass(SyntaxNode node)
    {
        return node is ClassDeclarationSyntax classDecl
            && classDecl.AttributeLists.Count > 0;
    }

    /// <summary>
    /// 核心生成逻辑。
    /// </summary>
    private static void Execute(
        Compilation compilation,
        ImmutableArray<ClassDeclarationSyntax> candidateClasses,
        SourceProductionContext context)
    {
        if (candidateClasses.IsDefaultOrEmpty)
            return;

        // ──────── 1. 分析插件入口类 ────────
        var pluginInfo = PluginClassAnalyzer.Analyze(
            compilation,
            context.ReportDiagnostic);

        if (pluginInfo == null)
            return;

        // ──────── 2. 全项目扫描 [Tool] 类 ────────
        var toolClasses = ToolClassAnalyzer.ScanAll(
            compilation,
            pluginInfo.Id,
            context.ReportDiagnostic);

        if (toolClasses.Count == 0)
            return;

        // ──────── 3. 工具名冲突检测 ────────
        ToolNameGenerator.CheckConflicts(toolClasses, context.ReportDiagnostic);

        // ──────── 4. 生成 plugin.json（通过 AddSource 落盘，由 .targets 复制到输出目录） ────────
        var pluginJson = PluginJsonEmitter.Emit(pluginInfo, compilation.AssemblyName);
        context.AddSource("plugin.json", pluginJson);

        // ──────── 5. 生成 Startup.g.cs ────────
        var startupSource = StartupEmitter.Emit(pluginInfo, toolClasses);
        context.AddSource("Startup.g.cs", startupSource);

        // ──────── 6. 检查自定义返回类型是否需要用户手写 PluginJsonContext ────────
        JsonContextEmitter.ValidateCustomReturnTypes(compilation, toolClasses, context.ReportDiagnostic);
    }
}