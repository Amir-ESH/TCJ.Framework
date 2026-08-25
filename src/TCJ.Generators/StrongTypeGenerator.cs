using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TCJ.Generators;

[Generator]
public sealed class StrongTypeGenerator : IIncrementalGenerator
{
    private const string StrongIdAttribute = "TCJ.Core.StrongTypes.StronglyTypedIdAttribute`1";
    private const string ValueObjectAttribute = "TCJ.Core.StrongTypes.ValueObjectAttribute`1";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<INamedTypeSymbol> types = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                StrongIdAttribute,
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => (INamedTypeSymbol)ctx.TargetSymbol)
            .Concat(context.SyntaxProvider.ForAttributeWithMetadataName(
                ValueObjectAttribute,
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => (INamedTypeSymbol)ctx.TargetSymbol));

        context.RegisterSourceOutput(types.Collect(), static (spc, symbols) =>
        {
            try
            {
                foreach (INamedTypeSymbol symbol in symbols
                    .Distinct(SymbolEqualityComparer.Default)
                    .OrderBy(static s => s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal))
                {
                    string hint = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                        .Replace("global::", string.Empty)
                        .Replace('.', '_')
                        .Replace('<', '_')
                        .Replace('>', '_') + ".g.cs";

                    spc.AddSource(hint, "// TCJ.Generators discovery only. No members are generated.");
                }
            }
            catch (Exception ex)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor(
                        "TCJG001",
                        "Generator failure",
                        ex.Message,
                        "TCJ.Generators",
                        DiagnosticSeverity.Error,
                        true),
                    Location.None));
            }
        });
    }
}
