using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TCJ.Analyzers.AotTrimming;

/// <summary>
/// Warns when reflection-based TCJ dependency scanning is used in AOT or trimmed projects.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ReflectionBasedDependencyScanningAnalyzer : DiagnosticAnalyzer
{
    internal const string DiagnosticId = "TCJ3000";

    private const string Category = "TCJ.AotTrimming";
    private const string ExtensionType = "TCJ.DependencyInjection.Extensions.ServiceCollectionExtensions";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Reflection-based TCJ dependency scanning is incompatible with AOT or trimming",
        "TCJ dependency scanning uses reflection and is not compatible with {0}. Use AddTcjDependencyInjection() and explicit Microsoft DI registrations instead",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "TCJ dependency scanning rules require AOT-compatible dependency registration patterns.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            AnalyzerConfigOptions options = start.Options.AnalyzerConfigOptionsProvider.GlobalOptions;
            bool aot = IsEnabled(options, "build_property.PublishAot");
            bool trimmed = IsEnabled(options, "build_property.PublishTrimmed");

            if (!aot && !trimmed)
            {
                return;
            }

            start.RegisterSyntaxNodeAction(
                node => AnalyzeInvocation(node, aot),
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.InvocationExpression);
        });
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, bool aot)
    {
        InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;
        IMethodSymbol? method = context.SemanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;

        if (method is null
            || method.ContainingType.ToDisplayString() != ExtensionType
            || method.Name != "AddTcjDependencyInjection")
        {
            return;
        }

        bool reflectionScanning = method.Parameters.Any(parameter =>
            parameter.Type.TypeKind == TypeKind.Array
            || parameter.Type.Name.IndexOf("Action", StringComparison.Ordinal) >= 0);

        if (!reflectionScanning)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            invocation.GetLocation(),
            aot ? "Native AOT" : "trimming"));
    }

    private static bool IsEnabled(AnalyzerConfigOptions options, string key)
        => options.TryGetValue(key, out string? value)
           && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
