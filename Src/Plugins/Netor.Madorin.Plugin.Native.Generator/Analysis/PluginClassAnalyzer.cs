using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Netor.Madorin.Plugin.Native.Generator.Diagnostics;

namespace Netor.Madorin.Plugin.Native.Generator.Analysis;

/// <summary>
/// 分析标记了 [Plugin] 的 static partial class Startup，提取插件元数据并验证约束。
/// </summary>
internal static class PluginClassAnalyzer
{
    private const string PluginAttributeName = "Netor.Madorin.Plugin.Native.PluginAttribute";
    private const string LegacyPluginAttributeName = "Netor.Cortana.Plugin.PluginAttribute";
    private const string RequiredHostCapabilityAttributeName = "Netor.Madorin.Plugin.Native.RequiredHostCapabilityAttribute";
    private const string LegacyRequiredHostCapabilityAttributeName = "Netor.Cortana.Plugin.RequiredHostCapabilityAttribute";
    private const string PluginSettingAttributeName = "Netor.Madorin.Plugin.Native.PluginSettingAttribute";
    private const string LegacyPluginSettingAttributeName = "Netor.Cortana.Plugin.PluginSettingAttribute";
    private const string PublishesEventAttributeName = "Netor.Madorin.Plugin.Native.PublishesEventAttribute";
    private const string SubscribesEventAttributeName = "Netor.Madorin.Plugin.Native.SubscribesEventAttribute";
    private const string ServiceCollectionInterfaceName = "Microsoft.Extensions.DependencyInjection.IServiceCollection";

    /// <summary>
    /// 从编译上下文中查找标记了 [Plugin] 的 static partial class，验证约束并提取元数据。
    /// </summary>
    public static PluginClassInfo? Analyze(
        Compilation compilation,
        Action<Diagnostic> reportDiagnostic)
    {
        var pluginClasses = new List<(ClassDeclarationSyntax Syntax, INamedTypeSymbol Symbol)>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            foreach (var classSyntax in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var classSymbol = semanticModel.GetDeclaredSymbol(classSyntax);
                if (classSymbol == null)
                    continue;

                var hasPluginAttr = classSymbol.GetAttributes().Any(IsPluginAttribute);
                if (!hasPluginAttr)
                    continue;

                pluginClasses.Add((classSyntax, classSymbol));
            }
        }

        if (pluginClasses.Count == 0)
            return null;

        if (pluginClasses.Count > 1)
        {
            var names = string.Join(", ", pluginClasses.Select(p => p.Symbol.Name));
            foreach (var (syntax, _) in pluginClasses)
            {
                reportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.MultiplePluginClasses,
                    syntax.Identifier.GetLocation(),
                    names));
            }
            return null;
        }

        var (pluginSyntax, pluginSymbol) = pluginClasses[0];

        bool isStatic = pluginSyntax.Modifiers.Any(SyntaxKind.StaticKeyword);
        bool isPartial = pluginSyntax.Modifiers.Any(SyntaxKind.PartialKeyword);

        if (!isStatic || !isPartial)
        {
            reportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.NotStaticPartialClass,
                pluginSyntax.Identifier.GetLocation(),
                pluginSymbol.Name));
            return null;
        }

        if (pluginSymbol.DeclaredAccessibility != Accessibility.Public)
        {
            reportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.NotPublicClass,
                pluginSyntax.Identifier.GetLocation(),
                pluginSymbol.Name));
            return null;
        }

        var configureMethod = pluginSymbol.GetMembers("Configure")
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m =>
                m.IsStatic
                && m.DeclaredAccessibility == Accessibility.Public
                && m.ReturnsVoid
                && m.Parameters.Length == 1
                && m.Parameters[0].Type.ToDisplayString() == ServiceCollectionInterfaceName);

        if (configureMethod == null)
        {
            reportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.MissingConfigureMethod,
                pluginSyntax.Identifier.GetLocation(),
                pluginSymbol.Name));
            return null;
        }

        var attrData = pluginSymbol.GetAttributes().First(IsPluginAttribute);
        var namedArgs = attrData.NamedArguments.ToDictionary(kv => kv.Key, kv => kv.Value);

        var id = GetNamedArgString(namedArgs, "Id");
        var name = GetNamedArgString(namedArgs, "Name");
        var version = GetNamedArgString(namedArgs, "Version") ?? "1.0.0";
        var description = GetNamedArgString(namedArgs, "Description") ?? "";
        var instructions = GetNamedArgString(namedArgs, "Instructions");

        string[] tags = Array.Empty<string>();
        if (namedArgs.TryGetValue("Tags", out var tagsValue) && !tagsValue.IsNull)
        {
            tags = tagsValue.Values
                .Where(v => v.Value is string)
                .Select(v => (string)v.Value!)
                .ToArray();
        }

        string[] capabilities = Array.Empty<string>();
        if (namedArgs.TryGetValue("Capabilities", out var capabilitiesValue) && !capabilitiesValue.IsNull)
        {
            capabilities = capabilitiesValue.Values
                .Where(v => v.Value is string)
                .Select(v => (string)v.Value!)
                .ToArray();
        }

        var requiredHostCapabilities = pluginSymbol.GetAttributes()
            .Where(IsRequiredHostCapabilityAttribute)
            .Select(ReadRequiredHostCapability)
            .Where(static capability => capability is not null)
            .Cast<RequiredHostCapabilityInfo>()
            .ToArray();

        var settingsSchema = pluginSymbol.GetAttributes()
            .Where(IsPluginSettingAttribute)
            .Select(attribute => ReadPluginSetting(attribute, reportDiagnostic))
            .Where(static setting => setting is not null)
            .Cast<PluginSettingInfo>()
            .ToArray();

        var publishedOps = pluginSymbol.GetAttributes()
            .Where(IsPublishesEventAttribute)
            .Select(ReadEventOpDeclaration)
            .Where(static op => op is not null)
            .Cast<EventOpDeclarationInfo>()
            .ToArray();

        var subscribedOps = pluginSymbol.GetAttributes()
            .Where(IsSubscribesEventAttribute)
            .Select(ReadEventOpDeclaration)
            .Where(static op => op is not null)
            .Cast<EventOpDeclarationInfo>()
            .ToArray();

        if (string.IsNullOrWhiteSpace(id))
        {
            reportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.MissingRequiredAttribute,
                pluginSyntax.Identifier.GetLocation(),
                "Id"));
            return null;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            reportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.MissingRequiredAttribute,
                pluginSyntax.Identifier.GetLocation(),
                "Name"));
            return null;
        }

        if (!IsValidPluginId(id!))
        {
            reportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.InvalidPluginId,
                pluginSyntax.Identifier.GetLocation(),
                id!));
            return null;
        }

        return new PluginClassInfo(pluginSymbol, id!, name!, version, description, tags, capabilities, requiredHostCapabilities, settingsSchema, publishedOps, subscribedOps, instructions);
    }

    private static bool IsValidPluginId(string id)
    {
        if (string.IsNullOrEmpty(id))
            return false;

        foreach (var c in id)
        {
            if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '_')
                return false;
        }

        return true;
    }

    private static string? GetNamedArgString(Dictionary<string, TypedConstant> args, string key)
    {
        if (args.TryGetValue(key, out var value) && value.Value is string s)
            return s;
        return null;
    }

    private static bool IsPluginAttribute(AttributeData attribute)
    {
        var name = attribute.AttributeClass?.ToDisplayString();
        return name == PluginAttributeName || name == LegacyPluginAttributeName;
    }

    private static bool IsRequiredHostCapabilityAttribute(AttributeData attribute)
    {
        var name = attribute.AttributeClass?.ToDisplayString();
        return name == RequiredHostCapabilityAttributeName || name == LegacyRequiredHostCapabilityAttributeName;
    }

    private static bool IsPluginSettingAttribute(AttributeData attribute)
    {
        var name = attribute.AttributeClass?.ToDisplayString();
        return name == PluginSettingAttributeName || name == LegacyPluginSettingAttributeName;
    }

    private static bool IsPublishesEventAttribute(AttributeData attribute)
        => attribute.AttributeClass?.ToDisplayString() == PublishesEventAttributeName;

    private static bool IsSubscribesEventAttribute(AttributeData attribute)
        => attribute.AttributeClass?.ToDisplayString() == SubscribesEventAttributeName;

    private static EventOpDeclarationInfo? ReadEventOpDeclaration(AttributeData attribute)
    {
        // [PublishesEvent("op", Description = "...")] / [SubscribesEvent("op", Reason = "...")]
        var op = attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is string opValue
            ? opValue
            : null;
        if (string.IsNullOrWhiteSpace(op))
        {
            return null;
        }

        var namedArgs = attribute.NamedArguments.ToDictionary(kv => kv.Key, kv => kv.Value);
        var description = GetNamedArgString(namedArgs, "Description")
            ?? GetNamedArgString(namedArgs, "Reason");

        return new EventOpDeclarationInfo(op!, description);
    }

    private static PluginSettingInfo? ReadPluginSetting(AttributeData attribute, Action<Diagnostic> reportDiagnostic)
    {
        var namedArgs = attribute.NamedArguments.ToDictionary(kv => kv.Key, kv => kv.Value);
        var key = GetNamedArgString(namedArgs, "Key");
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var type = GetNamedArgSettingType(namedArgs, "Type");
        var defaultValue = GetNamedArgString(namedArgs, "DefaultValue");
        if (type == "json"
            && !string.IsNullOrWhiteSpace(defaultValue)
            && !JsonValueValidator.IsValid(defaultValue!))
        {
            reportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.InvalidPluginSettingJsonDefaultValue,
                GetAttributeLocation(attribute),
                key));
            return null;
        }

        return new PluginSettingInfo(
            key!,
            GetNamedArgString(namedArgs, "Label"),
            GetNamedArgString(namedArgs, "Description"),
            type,
            defaultValue,
            GetNamedArgBool(namedArgs, "Required"),
            GetNamedArgBool(namedArgs, "Sensitive"),
            GetNamedArgStringArray(namedArgs, "Scope"),
            GetNamedArgStringArray(namedArgs, "Options"),
            GetNamedArgFiniteDouble(namedArgs, "ValidationMin"),
            GetNamedArgFiniteDouble(namedArgs, "ValidationMax"),
            GetNamedArgNonNegativeInt(namedArgs, "ValidationMinLength"),
            GetNamedArgNonNegativeInt(namedArgs, "ValidationMaxLength"),
            GetNamedArgString(namedArgs, "ValidationPattern"),
            GetNamedArgBool(namedArgs, "RestartRequired"));
    }

    private static Location GetAttributeLocation(AttributeData attribute)
    {
        return attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? Location.None;
    }

    private static RequiredHostCapabilityInfo? ReadRequiredHostCapability(AttributeData attribute)
    {
        var namedArgs = attribute.NamedArguments.ToDictionary(kv => kv.Key, kv => kv.Value);
        var id = GetNamedArgString(namedArgs, "Id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return new RequiredHostCapabilityInfo(
            id!,
            GetNamedArgString(namedArgs, "Purpose"),
            GetNamedArgBool(namedArgs, "Required"),
            GetNamedArgString(namedArgs, "Reason"));
    }

    private static bool GetNamedArgBool(Dictionary<string, TypedConstant> args, string key)
    {
        return args.TryGetValue(key, out var value) && value.Value is bool b && b;
    }

    private static string[] GetNamedArgStringArray(Dictionary<string, TypedConstant> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value.IsNull)
        {
            return Array.Empty<string>();
        }

        return value.Values
            .Where(v => v.Value is string)
            .Select(v => (string)v.Value!)
            .ToArray();
    }

    private static double? GetNamedArgFiniteDouble(Dictionary<string, TypedConstant> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value.Value is not double d || double.IsNaN(d) || double.IsInfinity(d))
        {
            return null;
        }

        return d;
    }

    private static int? GetNamedArgNonNegativeInt(Dictionary<string, TypedConstant> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value.Value is not int i || i < 0)
        {
            return null;
        }

        return i;
    }

    private static string GetNamedArgSettingType(Dictionary<string, TypedConstant> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value.Value is not int typeValue)
        {
            return "string";
        }

        switch (typeValue)
        {
            case 1:
                return "secret";
            case 2:
                return "number";
            case 3:
                return "boolean";
            case 4:
                return "enum";
            case 5:
                return "path";
            case 6:
                return "json";
            default:
                return "string";
        }
    }
}
