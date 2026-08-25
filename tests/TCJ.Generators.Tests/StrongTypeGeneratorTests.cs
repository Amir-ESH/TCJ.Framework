using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TCJ.Generators;

namespace TCJ.Generators.Tests;

public sealed class StrongTypeGeneratorTests
{
    [Fact]
    public void Generator_DiscoversAttributedDeclarations_WithoutGeneratingMembers()
    {
        var compilation = CreateCompilation(
            """
            namespace TCJ.Core.StrongTypes;

            [System.AttributeUsage(System.AttributeTargets.Struct)]
            public sealed class StronglyTypedIdAttribute<T> : System.Attribute
            {
            }
            """,
            """
            using TCJ.Core.StrongTypes;

            [StronglyTypedId<int>]
            public partial struct OrderId
            {
            }
            """);

        var result = RunGenerator(compilation);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.GeneratedSources);
    }

    [Fact]
    public void Generator_Output_IsStable_When_UnrelatedSyntaxChangesAreAdded()
    {
        var withoutUnrelatedSyntax = CreateCompilation(
            AttributeSource,
            """
            using TCJ.Core.StrongTypes;

            [StronglyTypedId<int>]
            public partial struct OrderId
            {
            }
            """);

        var withUnrelatedSyntax = CreateCompilation(
            AttributeSource,
            """
            class UnrelatedType
            {
            }

            using TCJ.Core.StrongTypes;

            [StronglyTypedId<int>]
            public partial struct OrderId
            {
            }
            """);

        var first = RunGenerator(withoutUnrelatedSyntax);
        var second = RunGenerator(withUnrelatedSyntax);

        Assert.Equal(first.GeneratedSources, second.GeneratedSources);
    }

    private static GeneratorResult RunGenerator(CSharpCompilation compilation)
    {
        var driver = CSharpGeneratorDriver.Create(new StrongTypeGenerator());
        driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out _,
            out var diagnostics);

        var runResult = driver.GetRunResult();

        return new GeneratorResult(
            diagnostics,
            runResult.Results
                .SelectMany(static result => result.GeneratedSources)
                .Select(static source => source.HintName)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray());
    }

    private static CSharpCompilation CreateCompilation(params string[] sources)
    {
        return CSharpCompilation.Create(
            "Test",
            sources.Select(CSharpSyntaxTree.ParseText),
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location)
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private sealed record GeneratorResult(
        IReadOnlyList<Diagnostic> Diagnostics,
        IReadOnlyList<string> GeneratedSources);

    private const string AttributeSource =
        """
        namespace TCJ.Core.StrongTypes;

        [System.AttributeUsage(System.AttributeTargets.Struct)]
        public sealed class StronglyTypedIdAttribute<T> : System.Attribute
        {
        }
        """;
}
