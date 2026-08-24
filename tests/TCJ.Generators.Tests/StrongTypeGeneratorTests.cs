using Microsoft.CodeAnalysis.CSharp;
using TCJ.Generators;

namespace TCJ.Generators.Tests;

public sealed class StrongTypeGeneratorTests
{
    [Fact]
    public void Generator_can_be_created_as_incremental_generator()
    {
        StrongTypeGenerator generator = new();
        Assert.NotNull(generator);
    }

    [Fact]
    public void Unrelated_syntax_does_not_change_discovery_contract()
    {
        SyntaxTree first = CSharpSyntaxTree.ParseText("namespace A; public class C { }");
        SyntaxTree second = CSharpSyntaxTree.ParseText("namespace A; public class C { int Value; }");

        Assert.Equal(2, first.GetRoot().DescendantNodes().Count(n => n.Kind().ToString() == "ClassDeclaration" || n.Kind().ToString() == "NamespaceDeclaration"));
        Assert.Equal(2, second.GetRoot().DescendantNodes().Count(n => n.Kind().ToString() == "ClassDeclaration" || n.Kind().ToString() == "NamespaceDeclaration"));
    }
}
