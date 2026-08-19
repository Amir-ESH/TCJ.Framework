using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using TCJ.Analyzers.CodeFixes.DependencyInjection;
using TCJ.Analyzers.DependencyInjection;
using TCJ.Analyzers.Tests.Infrastructure;
using TCJ.Core.DomainEvents;
using TCJ.DependencyInjection.Lifetimes;

namespace TCJ.Analyzers.Tests;

public sealed class ConflictingDependencyLifetimeMarkersAnalyzerTests
{
    private static readonly string[] TcjReferenceAssemblyPaths =
    [
        typeof(ITransientDependency).Assembly.Location,
        typeof(IDomainEvent).Assembly.Location,
    ];

    private static readonly string[] MarkerOrder =
    [
        nameof(ITransientDependency),
        nameof(IScopedDependency),
        nameof(ISingletonDependency),
        nameof(ISelfTransientDependency),
        nameof(ISelfScopedDependency),
        nameof(ISelfSingletonDependency),
    ];

    public static TheoryData<string, string> PairwiseLifetimeMarkers
    {
        get
        {
            TheoryData<string, string> data = new();

            for (int first = 0; first < MarkerOrder.Length; first++)
            {
                for (int second = first + 1; second < MarkerOrder.Length; second++)
                {
                    data.Add(MarkerOrder[first], MarkerOrder[second]);
                }
            }

            return data;
        }
    }

    public static TheoryData<string, string> CodeFixSelections => new()
    {
        { nameof(ITransientDependency), nameof(IScopedDependency) },
        { nameof(IScopedDependency), nameof(ITransientDependency) },
        { nameof(ISingletonDependency), nameof(IScopedDependency) },
        { nameof(ISelfTransientDependency), nameof(ISelfScopedDependency) },
        { nameof(ISelfScopedDependency), nameof(ISelfTransientDependency) },
        { nameof(ISelfSingletonDependency), nameof(ISelfScopedDependency) },
    };

    [Theory]
    [MemberData(nameof(PairwiseLifetimeMarkers))]
    public async Task Reports_every_direct_pairwise_lifetime_conflict_as_an_error(
        string firstMarker,
        string secondMarker)
    {
        string source = $$"""
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public interface IServiceContract;

            public sealed class Example : IServiceContract, {{secondMarker}}, {{firstMarker}};
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("TCJ0001", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("Example", source.Substring(
            diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length));

        string[] orderedMarkers = new[] { firstMarker, secondMarker }
            .OrderBy(marker => Array.IndexOf(MarkerOrder, marker))
            .ToArray();
        Assert.Contains(
            string.Join(", ", orderedMarkers),
            diagnostic.GetMessage(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_markers_in_runtime_order_regardless_of_interface_declaration_order()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public sealed class Example : ISelfScopedDependency, ITransientDependency, ISingletonDependency;
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Contains(
            "ITransientDependency, ISingletonDependency, ISelfScopedDependency",
            diagnostic.GetMessage(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_inherited_marker_interfaces()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public interface ICustomTransientDependency : ITransientDependency;

            public sealed class Example : ICustomTransientDependency, IScopedDependency;
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Contains(
            "ITransientDependency, IScopedDependency",
            diagnostic.GetMessage(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_markers_inherited_from_a_base_class()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public abstract class DependencyBase : ITransientDependency;

            public sealed class Example : DependencyBase, ISelfSingletonDependency;
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Contains(
            "ITransientDependency, ISelfSingletonDependency",
            diagnostic.GetMessage(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Does_not_report_a_valid_single_marker()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public interface IServiceContract;

            public sealed class Example : IServiceContract, IScopedDependency;
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Does_not_report_public_nested_types_inside_non_public_containers()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            internal static class Container
            {
                public sealed class Example : ITransientDependency, IScopedDependency;
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Preserves_runtime_scanner_boundaries_for_non_public_abstract_and_domain_event_handler_types()
    {
        const string source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using System.Runtime.CompilerServices;
            using TCJ.Core.DomainEvents;
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            internal sealed class InternalDependency : ITransientDependency, IScopedDependency;

            public abstract class AbstractDependency : ITransientDependency, IScopedDependency;

            [CompilerGenerated]
            public sealed class GeneratedDependency : ITransientDependency, IScopedDependency;

            public sealed class TestEvent : IDomainEvent
            {
                public DateTimeOffset OccurredOn => default;
            }

            public sealed class Handler :
                IDomainEventHandler<TestEvent>,
                ITransientDependency,
                IScopedDependency
            {
                public Task HandleAsync(TestEvent domainEvent, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Theory]
    [MemberData(nameof(CodeFixSelections))]
    public async Task Code_fix_keeps_the_selected_marker_and_preserves_non_TCJ_interfaces(
        string selectedMarker,
        string conflictingMarker)
    {
        string source = $$"""
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public interface IServiceContract;

            public sealed class Example : IServiceContract, {{selectedMarker}}, {{conflictingMarker}};
            """;

        string fixedSource = await AnalyzerTestHost.ApplyCodeFixAsync(
            source,
            new ConflictingDependencyLifetimeMarkersAnalyzer(),
            new ConflictingDependencyLifetimeMarkersCodeFixProvider(),
            $"Keep {selectedMarker}",
            TcjReferenceAssemblyPaths);

        Assert.Contains("IServiceContract", fixedSource, StringComparison.Ordinal);
        Assert.Contains(selectedMarker, fixedSource, StringComparison.Ordinal);
        Assert.DoesNotContain(conflictingMarker, fixedSource, StringComparison.Ordinal);
        AssertCompilesWithoutErrors(fixedSource);
        Assert.Empty(await GetDiagnosticsAsync(fixedSource));
    }

    [Fact]
    public async Task Code_fix_preserves_custom_inherited_interfaces_and_offers_only_safe_local_choice()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public interface IServiceContract;
            public interface ICustomTransientDependency : ITransientDependency;

            public sealed class Example : IServiceContract, ICustomTransientDependency, IScopedDependency;
            """;

        ConflictingDependencyLifetimeMarkersAnalyzer analyzer = new();
        ConflictingDependencyLifetimeMarkersCodeFixProvider provider = new();

        ImmutableArray<string> titles = await AnalyzerTestHost.GetCodeFixTitlesAsync(
            source,
            analyzer,
            provider,
            TcjReferenceAssemblyPaths);

        Assert.Equal(new[] { "Keep ITransientDependency" }, titles);

        string fixedSource = await AnalyzerTestHost.ApplyFirstCodeFixAsync(
            source,
            analyzer,
            provider,
            TcjReferenceAssemblyPaths);

        Assert.Contains("IServiceContract", fixedSource, StringComparison.Ordinal);
        Assert.Contains("ICustomTransientDependency", fixedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IScopedDependency", fixedSource, StringComparison.Ordinal);
        AssertCompilesWithoutErrors(fixedSource);
        Assert.Empty(await GetDiagnosticsAsync(fixedSource));
    }

    [Fact]
    public async Task Code_fix_actions_are_deterministic_and_support_fix_all()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public sealed class Example : IScopedDependency, ITransientDependency;
            """;

        ConflictingDependencyLifetimeMarkersCodeFixProvider provider = new();

        ImmutableArray<string> titles = await AnalyzerTestHost.GetCodeFixTitlesAsync(
            source,
            new ConflictingDependencyLifetimeMarkersAnalyzer(),
            provider,
            TcjReferenceAssemblyPaths);

        Assert.Equal(
            new[] { "Keep ITransientDependency", "Keep IScopedDependency" },
            titles);
        Assert.Same(WellKnownFixAllProviders.BatchFixer, provider.GetFixAllProvider());
    }

    private static Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(string source)
        => AnalyzerTestHost.GetAnalyzerDiagnosticsAsync(
            source,
            new ConflictingDependencyLifetimeMarkersAnalyzer(),
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
