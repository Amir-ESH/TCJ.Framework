using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using TCJ.Analyzers.CodeFixes.DependencyInjection;
using TCJ.Analyzers.DependencyInjection;
using TCJ.Analyzers.Tests.Infrastructure;
using TCJ.Core.DomainEvents;
using TCJ.DependencyInjection.Lifetimes;

namespace TCJ.Analyzers.Tests;

public sealed class InterfaceLifetimeMarkerWithoutServiceContractAnalyzerTests
{
    private static readonly string[] TcjReferenceAssemblyPaths =
    [
        typeof(ITransientDependency).Assembly.Location,
        typeof(IDomainEvent).Assembly.Location,
    ];

    public static TheoryData<string, string> InterfaceMarkerMappings => new()
    {
        { nameof(ITransientDependency), nameof(ISelfTransientDependency) },
        { nameof(IScopedDependency), nameof(ISelfScopedDependency) },
        { nameof(ISingletonDependency), nameof(ISelfSingletonDependency) },
    };

    [Theory]
    [MemberData(nameof(InterfaceMarkerMappings))]
    public async Task Reports_interface_registration_marker_when_no_service_interface_exists(
        string marker,
        string selfMarker)
    {
        string source = $$"""
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public sealed class Example : {{marker}};
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("TCJ0002", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("Example", source.Substring(
            diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length));
        Assert.Contains(marker, diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains(selfMarker, diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_marker_inherited_from_a_base_class_when_no_service_interface_exists()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public abstract class DependencyBase : IScopedDependency;

            public sealed class Example : DependencyBase;
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("TCJ0002", diagnostic.Id);
        Assert.Contains("IScopedDependency", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inherited_service_interface_prevents_a_false_positive()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public interface IServiceContract;

            public abstract class DependencyBase : IServiceContract, IScopedDependency;

            public sealed class Example : DependencyBase;
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Does_not_report_when_one_valid_service_interface_exists()
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
    public async Task Does_not_report_when_multiple_valid_service_interfaces_exist()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public interface IFirstService;
            public interface ISecondService;

            public sealed class Example : IFirstService, ISecondService, ISingletonDependency;
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Generic_service_interface_is_eligible_for_open_generic_dependency()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public interface IRepository<T>;

            public sealed class Repository<T> : IRepository<T>, ITransientDependency;
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Reports_when_only_disposal_interfaces_are_exposed()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public sealed class Example : IDisposable, IAsyncDisposable, IScopedDependency
            {
                public void Dispose() { }

                public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            }
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("TCJ0002", diagnostic.Id);
    }

    [Fact]
    public async Task Dependency_derived_interfaces_are_not_service_contracts()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public interface ICustomDependency : IScopedDependency;

            public sealed class Example : ICustomDependency;
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("TCJ0002", diagnostic.Id);
        Assert.Contains("IScopedDependency", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disposal_derived_custom_interface_remains_a_valid_runtime_service_contract()
    {
        const string source = """
            using System;
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public interface IManagedResource : IDisposable;

            public sealed class Example : IManagedResource, IScopedDependency
            {
                public void Dispose() { }
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Does_not_report_self_registration_markers()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public sealed class Transient : ISelfTransientDependency;
            public sealed class Scoped : ISelfScopedDependency;
            public sealed class Singleton : ISelfSingletonDependency;
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Does_not_cascade_on_types_with_multiple_lifetime_markers()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public sealed class Example : IScopedDependency, ISingletonDependency;
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Domain_event_handlers_are_excluded_from_this_diagnostic()
    {
        const string source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using TCJ.Core.DomainEvents;
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public sealed class TestEvent : IDomainEvent
            {
                public DateTimeOffset OccurredOn => default;
            }

            public sealed class Handler : IDomainEventHandler<TestEvent>, ITransientDependency
            {
                public Task HandleAsync(TestEvent domainEvent, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
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
                public sealed class Example : IScopedDependency;
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Preserves_runtime_scanner_public_concrete_and_generated_type_boundaries()
    {
        const string source = """
            using System.Runtime.CompilerServices;
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            internal sealed class InternalDependency : IScopedDependency;

            public abstract class AbstractDependency : IScopedDependency;

            [CompilerGenerated]
            public sealed class GeneratedDependency : IScopedDependency;
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Theory]
    [MemberData(nameof(InterfaceMarkerMappings))]
    public async Task Code_fix_switches_to_corresponding_self_marker_and_preserves_lifetime(
        string marker,
        string selfMarker)
    {
        string source = $$"""
            using System;
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public sealed partial class Example : IDisposable, {{marker}}
            {
                public void Dispose() { }
            }
            """;

        string fixedSource = await AnalyzerTestHost.ApplyCodeFixAsync(
            source,
            new InterfaceLifetimeMarkerWithoutServiceContractAnalyzer(),
            new InterfaceLifetimeMarkerWithoutServiceContractCodeFixProvider(),
            $"Use {selfMarker}",
            TcjReferenceAssemblyPaths);

        Assert.Contains("public sealed partial class Example", fixedSource, StringComparison.Ordinal);
        Assert.Contains("IDisposable", fixedSource, StringComparison.Ordinal);
        Assert.Contains(selfMarker, fixedSource, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, fixedSource, StringComparison.Ordinal);
        AssertCompilesWithoutErrors(fixedSource);
        Assert.Empty(await GetDiagnosticsAsync(fixedSource));
    }

    [Fact]
    public async Task Code_fix_preserves_qualification_when_no_lifetime_namespace_is_imported()
    {
        const string source = """
            using System;

            namespace Sample;

            public sealed class Example : IDisposable, TCJ.DependencyInjection.Lifetimes.IScopedDependency
            {
                public void Dispose() { }
            }
            """;

        string fixedSource = await AnalyzerTestHost.ApplyFirstCodeFixAsync(
            source,
            new InterfaceLifetimeMarkerWithoutServiceContractAnalyzer(),
            new InterfaceLifetimeMarkerWithoutServiceContractCodeFixProvider(),
            TcjReferenceAssemblyPaths);

        Assert.Contains(
            "TCJ.DependencyInjection.Lifetimes.ISelfScopedDependency",
            fixedSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TCJ.DependencyInjection.Lifetimes.IScopedDependency",
            fixedSource,
            StringComparison.Ordinal);
        AssertCompilesWithoutErrors(fixedSource);
    }

    [Fact]
    public async Task Inherited_marker_is_reported_but_no_unsafe_local_fix_is_offered()
    {
        const string source = """
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public interface ICustomScopedDependency : IScopedDependency;

            public sealed class Example : ICustomScopedDependency;
            """;

        InterfaceLifetimeMarkerWithoutServiceContractAnalyzer analyzer = new();
        InterfaceLifetimeMarkerWithoutServiceContractCodeFixProvider provider = new();

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));
        Assert.Equal("TCJ0002", diagnostic.Id);

        ImmutableArray<string> titles = await AnalyzerTestHost.GetCodeFixTitlesAsync(
            source,
            analyzer,
            provider,
            TcjReferenceAssemblyPaths);

        Assert.Empty(titles);
    }

    [Fact]
    public void Code_fix_supports_deterministic_fix_all()
    {
        InterfaceLifetimeMarkerWithoutServiceContractCodeFixProvider provider = new();

        Assert.Same(WellKnownFixAllProviders.BatchFixer, provider.GetFixAllProvider());
    }

    private static Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(string source)
        => AnalyzerTestHost.GetAnalyzerDiagnosticsAsync(
            source,
            new InterfaceLifetimeMarkerWithoutServiceContractAnalyzer(),
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
