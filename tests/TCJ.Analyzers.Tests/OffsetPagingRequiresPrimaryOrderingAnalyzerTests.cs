using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using TCJ.Analyzers.CodeFixes.DependencyInjection;
using TCJ.Analyzers.Specifications;
using TCJ.Analyzers.Tests.Infrastructure;
using TCJ.Core.Entities;
using TCJ.EntityFrameworkCore.Specifications;

namespace TCJ.Analyzers.Tests;

public sealed class OffsetPagingRequiresPrimaryOrderingAnalyzerTests
{
    private static readonly string[] TcjReferenceAssemblyPaths =
    [
        typeof(IEntity).Assembly.Location,
        typeof(Specification<>).Assembly.Location,
    ];

    [Fact]
    public async Task Reports_clearly_unordered_paging_in_constructor()
    {
        const string source = """
            using TCJ.EntityFrameworkCore.Specifications;

            namespace Sample;

            public sealed class Product { }

            public sealed class ProductSpecification : Specification<Product>
            {
                public ProductSpecification(int skip, int take)
                {
                    ApplyPaging(skip, take);
                }
            }
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("TCJ2000", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("ApplyPaging", GetDiagnosticText(source, diagnostic));
        Assert.Contains("ApplyOrderBy/ApplyOrderByDescending", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("ApplyThenBy/ApplyThenByDescending", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Does_not_report_ascending_primary_order()
    {
        const string source = """
            using TCJ.EntityFrameworkCore.Specifications;

            namespace Sample;

            public sealed class Product
            {
                public int Id { get; init; }
            }

            public sealed class ProductSpecification : Specification<Product>
            {
                public ProductSpecification(int skip, int take)
                {
                    ApplyOrderBy(product => product.Id);
                    ApplyPaging(skip, take);
                }
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Does_not_report_descending_primary_order()
    {
        const string source = """
            using TCJ.EntityFrameworkCore.Specifications;

            namespace Sample;

            public sealed class Product
            {
                public int Id { get; init; }
            }

            public sealed class ProductSpecification : Specification<Product>
            {
                public ProductSpecification(int skip, int take)
                {
                    ApplyOrderByDescending(product => product.Id);
                    ApplyPaging(skip, take);
                }
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Does_not_report_secondary_order_after_primary_order()
    {
        const string source = """
            using TCJ.EntityFrameworkCore.Specifications;

            namespace Sample;

            public sealed class Product
            {
                public string Name { get; init; } = string.Empty;
                public int Id { get; init; }
            }

            public sealed class ProductSpecification : Specification<Product>
            {
                public ProductSpecification(int skip, int take)
                {
                    ApplyOrderBy(product => product.Name);
                    ApplyThenBy(product => product.Id);
                    ApplyPaging(skip, take);
                }
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Reports_when_only_secondary_order_is_present()
    {
        const string source = """
            using TCJ.EntityFrameworkCore.Specifications;

            namespace Sample;

            public sealed class Product
            {
                public int Id { get; init; }
            }

            public sealed class ProductSpecification : Specification<Product>
            {
                public ProductSpecification(int skip, int take)
                {
                    ApplyThenBy(product => product.Id);
                    ApplyPaging(skip, take);
                }
            }
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("ApplyPaging", GetDiagnosticText(source, diagnostic));
    }

    [Fact]
    public async Task Does_not_report_when_primary_order_is_configured_after_paging()
    {
        const string source = """
            using TCJ.EntityFrameworkCore.Specifications;

            namespace Sample;

            public sealed class Product
            {
                public int Id { get; init; }
            }

            public sealed class ProductSpecification : Specification<Product>
            {
                public ProductSpecification(int skip, int take)
                {
                    ApplyPaging(skip, take);
                    ApplyOrderBy(product => product.Id);
                }
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Reports_paging_in_direct_linear_initialization_helper()
    {
        const string source = """
            using TCJ.EntityFrameworkCore.Specifications;

            namespace Sample;

            public sealed class Product { }

            public sealed class ProductSpecification : Specification<Product>
            {
                public ProductSpecification()
                {
                    ConfigurePaging();
                }

                private void ConfigurePaging()
                {
                    ApplyPaging(0, 25);
                }
            }
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("ApplyPaging", GetDiagnosticText(source, diagnostic));
    }

    [Fact]
    public async Task Does_not_report_when_direct_linear_helpers_establish_primary_order()
    {
        const string source = """
            using TCJ.EntityFrameworkCore.Specifications;

            namespace Sample;

            public sealed class Product
            {
                public int Id { get; init; }
            }

            public sealed class ProductSpecification : Specification<Product>
            {
                public ProductSpecification()
                {
                    ConfigureOrdering();
                    ConfigurePaging();
                }

                private void ConfigureOrdering() => ApplyOrderBy(product => product.Id);

                private void ConfigurePaging() => ApplyPaging(0, 25);
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Declines_to_judge_helper_control_flow()
    {
        const string source = """
            using TCJ.EntityFrameworkCore.Specifications;

            namespace Sample;

            public sealed class Product
            {
                public int Id { get; init; }
            }

            public sealed class ProductSpecification : Specification<Product>
            {
                public ProductSpecification(bool orderById)
                {
                    ConfigureOrdering(orderById);
                    ApplyPaging(0, 25);
                }

                private void ConfigureOrdering(bool orderById)
                {
                    if (orderById)
                    {
                        ApplyOrderBy(product => product.Id);
                    }
                }
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Method_name_lookalike_does_not_count_as_TCJ_primary_order()
    {
        const string source = """
            using TCJ.EntityFrameworkCore.Specifications;

            namespace Sample;

            public sealed class Product { }

            public sealed class ProductSpecification : Specification<Product>
            {
                public ProductSpecification()
                {
                    ApplyOrderBy("not a TCJ order expression");
                    ApplyPaging(0, 25);
                }

                private void ApplyOrderBy(string value)
                {
                }
            }
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("ApplyPaging", GetDiagnosticText(source, diagnostic));
    }

    [Fact]
    public async Task Does_not_report_specifications_without_paging()
    {
        const string source = """
            using TCJ.EntityFrameworkCore.Specifications;

            namespace Sample;

            public sealed class Product
            {
                public int Id { get; init; }
            }

            public sealed class ProductSpecification : Specification<Product>
            {
                public ProductSpecification()
                {
                    ApplyOrderBy(product => product.Id);
                }
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Declines_to_judge_custom_specification_base_constructor_paths()
    {
        const string source = """
            using TCJ.EntityFrameworkCore.Specifications;

            namespace Sample;

            public sealed class Product { }

            public abstract class ProductSpecificationBase : Specification<Product>
            {
            }

            public sealed class ProductSpecification : ProductSpecificationBase
            {
                public ProductSpecification()
                {
                    ApplyPaging(0, 25);
                }
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public void No_code_fix_provider_claims_TCJ2000()
    {
        Type[] providerTypes = typeof(ConflictingDependencyLifetimeMarkersCodeFixProvider).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(CodeFixProvider).IsAssignableFrom(type))
            .ToArray();

        foreach (Type providerType in providerTypes)
        {
            CodeFixProvider provider = Assert.IsAssignableFrom<CodeFixProvider>(
                Activator.CreateInstance(providerType));

            Assert.DoesNotContain("TCJ2000", provider.FixableDiagnosticIds);
        }
    }

    private static Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(string source) =>
        AnalyzerTestHost.GetAnalyzerDiagnosticsAsync(
            source,
            new OffsetPagingRequiresPrimaryOrderingAnalyzer(),
            TcjReferenceAssemblyPaths);

    private static string GetDiagnosticText(string source, Diagnostic diagnostic) =>
        source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length);
}
