using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TCJ.Generators;

namespace TCJ.Generators.Tests;

public sealed class StrongTypeGeneratorTests
{
    [Fact]
    public void Generator_DiscoversAttributedDeclarations_WithoutGeneratingMembers()
    {
        var syntaxTree = CSharpSyntaxTree.ParseText("[TCJ.Core.StrongTypes.StronglyTypedId<int>] public partial struct OrderId { }");
        var compilation = CSharpCompilation.Create("Test", new[] { syntaxTree });
        var driver = CSharpGeneratorDriver.Create(new StrongTypeGenerator());

        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics);

        Assert.Empty(diagnostics);
        Assert.Empty(updated.SyntaxTrees.Skip(1));
    }
}
