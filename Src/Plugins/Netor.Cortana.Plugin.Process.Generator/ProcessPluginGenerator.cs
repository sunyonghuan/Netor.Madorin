using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Netor.Cortana.Plugin.Process.Generator.Analysis;
using Netor.Cortana.Plugin.Process.Generator.Emitters;

using System.Collections.Immutable;

namespace Netor.Cortana.Plugin.Process.Generator;

/// <summary>
/// Process 插件框架的 Roslyn Incremental Source Generator 入口。
/// <para>
/// 扫描标记了 <c>[Plugin]</c> 的 partial class（通常是 <c>Program</c>）和所有 <c>[Tool]</c> 类，
/// 自动生成：
/// <list type="bullet">
///   <item><c>Program.g.cs</c>：消息循环入口 + 工具路由字典 + DI 注册委托</item>
///   <item><c>{PluginClass}Debugger.g.cs</c>：强类型 Debugger 类（测试用）</item>
///   <item><c>plugin.json</c>：宿主加载清单</item>
/// </list>
/// </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ProcessPluginGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsCandidateClass(node),
                transform: static (ctx, _) => (ClassDeclarationSyntax)ctx.Node)
            .Collect();

        var combined = context.CompilationProvider.Combine(classDeclarations);

        context.RegisterSourceOutput(combined, static (spc, source) =>
        {
            var (compilation, candidates) = source;
            Execute(compilation, candidates, spc);
        });
    }

    private static bool IsCandidateClass(SyntaxNode node)
        => node is ClassDeclarationSyntax classDecl && classDecl.AttributeLists.Count > 0;

    private static void Execute(
        Compilation compilation,
        ImmutableArray<ClassDeclarationSyntax> candidateClasses,
        SourceProductionContext context)
    {
        if (candidateClasses.IsDefaultOrEmpty)
            return;

        // 1. 分析插件入口类
        var pluginInfo = PluginClassAnalyzer.Analyze(compilation, context.ReportDiagnostic);
        if (pluginInfo == null) return;

        // 2. 全项目扫描 [Tool] 类
        var toolClasses = ToolClassAnalyzer.ScanAll(compilation, pluginInfo.Id, context.ReportDiagnostic);
        if (toolClasses.Count == 0) return;

        // 3. 工具名冲突检测
        ToolNameGenerator.CheckConflicts(toolClasses, context.ReportDiagnostic);

        // 4. 生成 plugin.json（AddSource 落盘，由 .targets 复制）
        var pluginJson = PluginJsonEmitter.Emit(pluginInfo, compilation.AssemblyName);
        context.AddSource("plugin.json", pluginJson);

        // 5. 生成 Program.g.cs
        var programSource = ProgramEmitter.Emit(pluginInfo, toolClasses);
        context.AddSource($"{pluginInfo.ClassName}.Program.g.cs", programSource);

        // 6. 生成 {PluginClass}Debugger.g.cs
        var debuggerSource = DebuggerEmitter.Emit(pluginInfo, toolClasses);
        context.AddSource($"{pluginInfo.ClassName}Debugger.g.cs", debuggerSource);
    }
}
