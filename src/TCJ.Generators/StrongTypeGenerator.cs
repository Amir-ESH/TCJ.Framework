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
        var strongIdCandidates = context.SyntaxProvider.ForAttributeWithMetadataName(
            StrongIdAttribute,
            static (node, _) => node is RecordDeclarationSyntax or StructDeclarationSyntax or ClassDeclarationSyntax,
            static (ctx, _) => ctx.TargetSymbol.ToDisplayString())
            .Collect();

        var valueObjectCandidates = context.SyntaxProvider.ForAttributeWithMetadataName(
            ValueObjectAttribute,
            static (node, _) => node is RecordDeclarationSyntax or StructDeclarationSyntax or ClassDeclarationSyntax,
            static (ctx, _) => ctx.TargetSymbol.ToDisplayString())
            .Collect();

        var candidates = strongIdCandidates.Combine(valueObjectCandidates);

        context.RegisterSourceOutput(candidates, static (spc, pair) =>
        {
            foreach (var symbol in pair.Left.Concat(pair.Right).OrderBy(static x => x, StringComparer.Ordinal))
            {
                _ = symbol;
            }
        });
    }
}
