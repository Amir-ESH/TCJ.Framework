using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using TCJ.Analyzers.CodeFixes.DependencyInjection;
using TCJ.Analyzers.DependencyInjection;
using TCJ.Analyzers.Tests.Infrastructure;
using TCJ.Core.DomainEvents;
using TCJ.DependencyInjection.Lifetimes;

namespace TCJ.Analyzers.Tests;

public sealed class ConventionDependencyMustBeEffectivelyPublicAnalyzerTests
{
    private static readonly string[] TcjReferenceAssemblyPaths =
    [
        typeof(ITransientDependency).Assembly.Location,
        typeof(IDomainEvent).Assembly.Location,
    ];

    [Theory]
    [InlineData("internal sealed class Example : IServiceContract, IScopedDependency;")]
    [InlineData("sealed class Example : IServiceContract, IScopedDependency;")]
    public async Task Reports_top_level_marked_types_that_are_not_public(string declaration)
    {
        string source = $$"""
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public interface IServiceContract;

            {{declaration}}
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("TCJ0003", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("Example", source.Substring(
            diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length));
        Assert.Contains("the type itself is not public", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ITransientDependency")]
    [InlineData("IScopedDependency")]
    [InlineData("ISingletonDependency")]
    [InlineData("ISelfTransientDependency")]
    [InlineData("ISelfScopedDependency")]
    [InlineData("ISelfSingletonDependency")]
    public async Task Reports_all_supported_lifetime_markers(string marker)
    {
        string source = $$"""
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            internal sealed class Example : {{marker}};
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("TCJ0003", diagnostic.Id);
    }

    [Fact]
    public async Task Reports_public_nested_type_when_a_containing_type_is_not_public()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public interface IServiceContract;

            internal static class Container
            {
                public sealed class Example : IServiceContract, IScopedDependency;
            }
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("TCJ0003", diagnostic.Id);
        Assert.Contains("containing type 'Container' is not public", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_non_public_nested_type_when_all_containing_types_are_public()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public interface IServiceContract;

            public static class Container
            {
                private sealed class Example : IServiceContract, IScopedDependency;
            }
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("TCJ0003", diagnostic.Id);
        Assert.Contains("the type itself is not public", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_marker_inherited_through_a_custom_interface()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public interface IServiceContract;
            public interface ICustomScopedDependency : IScopedDependency;

            internal sealed class Example : IServiceContract, ICustomScopedDependency;
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("TCJ0003", diagnostic.Id);
    }

    [Fact]
    public async Task Does_not_report_effectively_public_marked_types()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public interface IServiceContract;

            public sealed class TopLevel : IServiceContract, IScopedDependency;

            public static class Outer
            {
                public static class Inner
                {
                    public sealed class Nested : IServiceContract, ITransientDependency;
                }
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Does_not_report_abstract_marked_base_types_or_unmarked_internal_types()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            internal abstract class DependencyBase : IScopedDependency;
            internal sealed class Helper;
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Domain_event_handlers_and_compiler_generated_types_are_excluded()
    {
        const string source = """
            using System;
            using System.Runtime.CompilerServices;
            using System.Threading;
            using System.Threading.Tasks;
            using TCJ.Core.DomainEvents;
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public sealed class TestEvent : IDomainEvent
            {
                public DateTimeOffset OccurredOn => default;
            }

            internal sealed class Handler : IDomainEventHandler<TestEvent>, ITransientDependency
            {
                public Task HandleAsync(TestEvent domainEvent, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            [CompilerGenerated]
            internal sealed class GeneratedDependency : ISelfScopedDependency;
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Code_fix_makes_a_top_level_type_public_without_changing_its_interfaces()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public interface IServiceContract;

            internal sealed class Example : IServiceContract, IScopedDependency;
            """;

        string fixedSource = await AnalyzerTestHost.ApplyFirstCodeFixAsync(
            source,
            new ConventionDependencyMustBeEffectivelyPublicAnalyzer(),
            new ConventionDependencyMustBeEffectivelyPublicCodeFixProvider(),
            TcjReferenceAssemblyPaths);

        Assert.Contains("public sealed class Example", fixedSource, StringComparison.Ordinal);
        Assert.Contains("IServiceContract", fixedSource, StringComparison.Ordinal);
        Assert.Contains("IScopedDependency", fixedSource, StringComparison.Ordinal);
        AssertCompilesWithoutErrors(fixedSource);
        Assert.Empty(await GetDiagnosticsAsync(fixedSource));
    }

    [Fact]
    public async Task Code_fix_makes_a_nested_type_public_when_its_containers_are_public()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public static class Container
            {
                private sealed class Example : ISelfSingletonDependency;
            }
            """;

        string fixedSource = await AnalyzerTestHost.ApplyFirstCodeFixAsync(
            source,
            new ConventionDependencyMustBeEffectivelyPublicAnalyzer(),
            new ConventionDependencyMustBeEffectivelyPublicCodeFixProvider(),
            TcjReferenceAssemblyPaths);

        Assert.Contains("public sealed class Example", fixedSource, StringComparison.Ordinal);
        AssertCompilesWithoutErrors(fixedSource);
        Assert.Empty(await GetDiagnosticsAsync(fixedSource));
    }

    [Fact]
    public async Task Code_fix_is_not_offered_when_a_containing_type_blocks_accessibility()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            internal static class Container
            {
                public sealed class Example : ISelfScopedDependency;
            }
            """;

        ImmutableArray<string> titles = await AnalyzerTestHost.GetCodeFixTitlesAsync(
            source,
            new ConventionDependencyMustBeEffectivelyPublicAnalyzer(),
            new ConventionDependencyMustBeEffectivelyPublicCodeFixProvider(),
            TcjReferenceAssemblyPaths);

        Assert.Empty(titles);
    }

    [Fact]
    public async Task Code_fix_is_not_offered_for_file_local_types()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            file sealed class Example : ISelfTransientDependency;
            """;

        ImmutableArray<string> titles = await AnalyzerTestHost.GetCodeFixTitlesAsync(
            source,
            new ConventionDependencyMustBeEffectivelyPublicAnalyzer(),
            new ConventionDependencyMustBeEffectivelyPublicCodeFixProvider(),
            TcjReferenceAssemblyPaths);

        Assert.Empty(titles);
    }

    [Fact]
    public async Task Code_fix_is_not_offered_for_partial_types_that_require_multiple_declaration_edits()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            internal sealed partial class Example : ISelfScopedDependency;
            internal sealed partial class Example;
            """;

        ImmutableArray<string> titles = await AnalyzerTestHost.GetCodeFixTitlesAsync(
            source,
            new ConventionDependencyMustBeEffectivelyPublicAnalyzer(),
            new ConventionDependencyMustBeEffectivelyPublicCodeFixProvider(),
            TcjReferenceAssemblyPaths);

        Assert.Empty(titles);
    }

    [Fact]
    public void Code_fix_supports_deterministic_fix_all()
    {
        ConventionDependencyMustBeEffectivelyPublicCodeFixProvider provider = new();

        Assert.Same(WellKnownFixAllProviders.BatchFixer, provider.GetFixAllProvider());
    }

    private static Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(string source)
        => AnalyzerTestHost.GetAnalyzerDiagnosticsAsync(
            source,
            new ConventionDependencyMustBeEffectivelyPublicAnalyzer(),
            TcjReferenceAssemblyPaths);

    private static void AssertCompilesWithoutErrors(string source)
    {
        ImmutableArray<Diagnostic> errors = AnalyzerTestHost
            .CreateCompilation(source, TcjReferenceAssemblyPaths)
            .GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

        Assert.Empty(errors);
    }
}
