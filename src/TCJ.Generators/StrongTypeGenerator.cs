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
        var candidates = context.SyntaxProvider.ForAttributeWithMetadataName(
            StrongIdAttribute,
            static (node, _) => node is RecordDeclarationSyntax or StructDeclarationSyntax or ClassDeclarationSyntax,
            static (ctx, _) => ctx.TargetSymbol.ToDisplayString())
            .Concat(context.SyntaxProvider.ForAttributeWithMetadataName(
                ValueObjectAttribute,
                static (node, _) => node is RecordDeclarationSyntax or StructDeclarationSyntax or ClassDeclarationSyntax,
                static (ctx, _) => ctx.TargetSymbol.ToDisplayString()))
            .Collect();

        context.RegisterSourceOutput(candidates, static (spc, symbols) =>
        {
            foreach (var symbol in symbols.OrderBy(static x => x, StringComparer.Ordinal))
            {
                _ = symbol;
            }
        });
    }
}
