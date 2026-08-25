using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using TCJ.Generators;

namespace TCJ.Generators.Tests;

public sealed class StrongTypeGeneratorTests
{
    [Fact]
    public void Generator_can_be_created_as_incremental_generator()
    {
        IIncrementalGenerator generator = new StrongTypeGenerator();

        Assert.NotNull(generator);
    }

    [Fact]
    public void Unrelated_syntax_changes_do_not_change_attribute_discovery_input()
    {
        SyntaxTree first = CSharpSyntaxTree.ParseText("namespace A; public partial record struct C;" );
        SyntaxTree second = CSharpSyntaxTree.ParseText("namespace A; public partial record struct C { private int Value; }");

        Assert.Equal(1, CountCandidateTypes(first));
        Assert.Equal(1, CountCandidateTypes(second));
    }

    private static int CountCandidateTypes(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes().Count(static node => node is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax);
}
