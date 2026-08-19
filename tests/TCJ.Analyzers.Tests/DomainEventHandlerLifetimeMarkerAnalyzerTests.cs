using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using TCJ.Analyzers.CodeFixes.DependencyInjection;
using TCJ.Analyzers.DependencyInjection;
using TCJ.Analyzers.Tests.Infrastructure;
using TCJ.Core.DomainEvents;
using TCJ.DependencyInjection.Lifetimes;

namespace TCJ.Analyzers.Tests;

public sealed class DomainEventHandlerLifetimeMarkerAnalyzerTests
{
    private static readonly string[] TcjReferenceAssemblyPaths =
    [
        typeof(ITransientDependency).Assembly.Location,
        typeof(IDomainEvent).Assembly.Location,
    ];

    public static TheoryData<string> LifetimeMarkers => new()
    {
        nameof(ITransientDependency),
        nameof(IScopedDependency),
        nameof(ISingletonDependency),
        nameof(ISelfTransientDependency),
        nameof(ISelfScopedDependency),
        nameof(ISelfSingletonDependency),
    };

    [Theory]
    [MemberData(nameof(LifetimeMarkers))]
    public async Task Reports_every_TCJ_lifetime_marker_family_on_domain_event_handlers(
        string marker)
    {
        string source = $$"""
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

            public sealed class Handler : IDomainEventHandler<TestEvent>, {{marker}}
            {
                public Task HandleAsync(TestEvent domainEvent, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("TCJ0004", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("Handler", source.Substring(
            diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length));
        Assert.Contains(marker, diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("registered by the handler pipeline", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_one_diagnostic_for_a_handler_implementing_multiple_event_types()
    {
        const string source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using TCJ.Core.DomainEvents;
            using TCJ.DependencyInjection.Lifetimes;

            namespace Sample;

            public sealed class FirstEvent : IDomainEvent
            {
                public DateTimeOffset OccurredOn => default;
            }

            public sealed class SecondEvent : IDomainEvent
            {
                public DateTimeOffset OccurredOn => default;
            }

            public sealed class Handler :
                IDomainEventHandler<FirstEvent>,
                IDomainEventHandler<SecondEvent>,
                IScopedDependency
            {
                public Task HandleAsync(FirstEvent domainEvent, CancellationToken cancellationToken)
                    => Task.CompletedTask;

                public Task HandleAsync(SecondEvent domainEvent, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Contains(nameof(IScopedDependency), diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_handlers_inheriting_the_domain_event_handler_contract()
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

            public abstract class HandlerBase : IDomainEventHandler<TestEvent>
            {
                public Task HandleAsync(TestEvent domainEvent, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }

            public sealed class Handler : HandlerBase, IScopedDependency
            {
            }
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("Handler", source.Substring(
            diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length));
    }

    [Fact]
    public async Task Reports_inherited_TCJ_lifetime_markers_but_does_not_offer_an_unsafe_local_fix()
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

            public interface ICustomScopedDependency : IScopedDependency
            {
            }

            public sealed class Handler : IDomainEventHandler<TestEvent>, ICustomScopedDependency
            {
                public Task HandleAsync(TestEvent domainEvent, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));
        Assert.Contains(nameof(IScopedDependency), diagnostic.GetMessage(), StringComparison.Ordinal);

        ImmutableArray<string> titles = await AnalyzerTestHost.GetCodeFixTitlesAsync(
            source,
            new DomainEventHandlerLifetimeMarkerAnalyzer(),
            new DomainEventHandlerLifetimeMarkerCodeFixProvider(),
            TcjReferenceAssemblyPaths);

        Assert.Empty(titles);
    }

    [Fact]
    public async Task Does_not_report_a_normal_handler_without_a_TCJ_lifetime_marker()
    {
        const string source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using TCJ.Core.DomainEvents;

            namespace Sample;

            public sealed class TestEvent : IDomainEvent
            {
                public DateTimeOffset OccurredOn => default;
            }

            public sealed class Handler : IDomainEventHandler<TestEvent>
            {
                public Task HandleAsync(TestEvent domainEvent, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Does_not_report_unrelated_marker_interfaces()
    {
        const string source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using TCJ.Core.DomainEvents;

            namespace Sample;

            public sealed class TestEvent : IDomainEvent
            {
                public DateTimeOffset OccurredOn => default;
            }

            public interface IHandlerMarker
            {
            }

            public sealed class Handler : IDomainEventHandler<TestEvent>, IHandlerMarker
            {
                public Task HandleAsync(TestEvent domainEvent, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Code_fix_removes_all_direct_TCJ_lifetime_markers_and_preserves_other_interfaces()
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

            public interface IServiceContract
            {
            }
            public interface IHandlerMarker
            {
            }

            public sealed class Handler :
                IServiceContract,
                IDomainEventHandler<TestEvent>,
                IHandlerMarker,
                ITransientDependency,
                ISelfSingletonDependency
            {
                public Task HandleAsync(TestEvent domainEvent, CancellationToken cancellationToken)
                    => Task.CompletedTask;
            }
            """;

        DomainEventHandlerLifetimeMarkerAnalyzer analyzer = new();
        DomainEventHandlerLifetimeMarkerCodeFixProvider provider = new();

        ImmutableArray<string> titles = await AnalyzerTestHost.GetCodeFixTitlesAsync(
            source,
            analyzer,
            provider,
            TcjReferenceAssemblyPaths);

        Assert.Equal(new[] { "Remove TCJ lifetime markers" }, titles);

        string fixedSource = await AnalyzerTestHost.ApplyFirstCodeFixAsync(
            source,
            analyzer,
            provider,
            TcjReferenceAssemblyPaths);

        Assert.Contains("IServiceContract", fixedSource, StringComparison.Ordinal);
        Assert.Contains("IDomainEventHandler<TestEvent>", fixedSource, StringComparison.Ordinal);
        Assert.Contains("IHandlerMarker", fixedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ITransientDependency", fixedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ISelfSingletonDependency", fixedSource, StringComparison.Ordinal);
        AssertCompilesWithoutErrors(fixedSource);
        Assert.Empty(await GetDiagnosticsAsync(fixedSource));
    }

    private static Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(string source)
        => AnalyzerTestHost.GetAnalyzerDiagnosticsAsync(
            source,
            new DomainEventHandlerLifetimeMarkerAnalyzer(),
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
