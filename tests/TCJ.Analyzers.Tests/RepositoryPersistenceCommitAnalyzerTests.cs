using System.Collections.Immutable;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.EntityFrameworkCore;
using TCJ.Analyzers.CodeFixes.DependencyInjection;
using TCJ.Analyzers.Persistence;
using TCJ.Analyzers.Tests.Infrastructure;
using TCJ.Core.Entities;
using TCJ.EntityFrameworkCore.Repositories;

namespace TCJ.Analyzers.Tests;

public sealed class RepositoryPersistenceCommitAnalyzerTests
{
    private static readonly string[] TcjReferenceAssemblyPaths =
    [
        typeof(IEntity).Assembly.Location,
        typeof(IRepository).Assembly.Location,
        typeof(DbContext).Assembly.Location,
    ];

    [Fact]
    public async Task Reports_DbContext_SaveChangesAsync_inside_repository()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.EntityFrameworkCore;
            using TCJ.EntityFrameworkCore.Repositories;

            namespace Sample;

            public sealed class OrderRepository : IRepository
            {
                private readonly DbContext _dbContext;

                public OrderRepository(DbContext dbContext)
                {
                    _dbContext = dbContext;
                }

                public Task<int> CommitAsync(CancellationToken cancellationToken) =>
                    _dbContext.SaveChangesAsync(cancellationToken);
            }
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("TCJ1000", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("SaveChangesAsync", GetDiagnosticText(source, diagnostic));
        Assert.Contains("DbContext.SaveChangesAsync", diagnostic.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("application/use-case boundary", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_DbContext_SaveChanges_inside_repository()
    {
        const string source = """
            using Microsoft.EntityFrameworkCore;
            using TCJ.EntityFrameworkCore.Repositories;

            namespace Sample;

            public sealed class OrderRepository : IRepository
            {
                private readonly DbContext _dbContext;

                public OrderRepository(DbContext dbContext)
                {
                    _dbContext = dbContext;
                }

                public int Commit() => _dbContext.SaveChanges();
            }
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("SaveChanges", GetDiagnosticText(source, diagnostic));
        Assert.Contains("DbContext.SaveChanges", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_overridden_DbContext_SaveChangesAsync_inside_repository()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.EntityFrameworkCore;
            using TCJ.EntityFrameworkCore.Repositories;

            namespace Sample;

            public sealed class TestDbContext : DbContext
            {
                public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
                    base.SaveChangesAsync(cancellationToken);
            }

            public sealed class OrderRepository : IRepository
            {
                private readonly TestDbContext _dbContext;

                public OrderRepository(TestDbContext dbContext)
                {
                    _dbContext = dbContext;
                }

                public Task<int> CommitAsync(CancellationToken cancellationToken) =>
                    _dbContext.SaveChangesAsync(cancellationToken);
            }
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("SaveChangesAsync", GetDiagnosticText(source, diagnostic));
        Assert.Contains("DbContext.SaveChangesAsync", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_IUnitOfWork_SaveChangesAsync_inside_repository()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using TCJ.EntityFrameworkCore.Repositories;
            using TCJ.EntityFrameworkCore.UnitOfWork;

            namespace Sample;

            public sealed class OrderRepository : IRepository
            {
                private readonly IUnitOfWork _unitOfWork;

                public OrderRepository(IUnitOfWork unitOfWork)
                {
                    _unitOfWork = unitOfWork;
                }

                public Task<int> CommitAsync(CancellationToken cancellationToken) =>
                    _unitOfWork.SaveChangesAsync(cancellationToken);
            }
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("SaveChangesAsync", GetDiagnosticText(source, diagnostic));
        Assert.Contains("IUnitOfWork.SaveChangesAsync", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_IUnitOfWork_commits_reached_through_inherited_interfaces()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using TCJ.EntityFrameworkCore.Repositories;
            using TCJ.EntityFrameworkCore.UnitOfWork;

            namespace Sample;

            public interface IApplicationUnitOfWork : IUnitOfWork
            {
            }

            public sealed class OrderRepository : IRepository
            {
                private readonly IApplicationUnitOfWork _unitOfWork;

                public OrderRepository(IApplicationUnitOfWork unitOfWork)
                {
                    _unitOfWork = unitOfWork;
                }

                public Task<int> CommitAsync(CancellationToken cancellationToken) =>
                    _unitOfWork.SaveChangesAsync(cancellationToken);
            }
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("SaveChangesAsync", GetDiagnosticText(source, diagnostic));
        Assert.Contains("IUnitOfWork.SaveChangesAsync", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_concrete_IUnitOfWork_implementation_calls_inside_repository()
    {
        const string source = """
            using System.Data;
            using System.Threading;
            using System.Threading.Tasks;
            using TCJ.EntityFrameworkCore.Repositories;
            using TCJ.EntityFrameworkCore.UnitOfWork;

            namespace Sample;

            public sealed class CustomUnitOfWork : IUnitOfWork
            {
                public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
                    Task.FromResult(0);

                public Task<IUnitOfWorkTransaction> BeginTransactionAsync(
                    CancellationToken cancellationToken = default) =>
                    throw new System.NotSupportedException();

                public Task<IUnitOfWorkTransaction> BeginTransactionAsync(
                    IsolationLevel isolationLevel,
                    CancellationToken cancellationToken = default) =>
                    throw new System.NotSupportedException();
            }

            public sealed class OrderRepository : IRepository
            {
                private readonly CustomUnitOfWork _unitOfWork;

                public OrderRepository(CustomUnitOfWork unitOfWork)
                {
                    _unitOfWork = unitOfWork;
                }

                public Task<int> CommitAsync(CancellationToken cancellationToken) =>
                    _unitOfWork.SaveChangesAsync(cancellationToken);
            }
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("SaveChangesAsync", GetDiagnosticText(source, diagnostic));
        Assert.Contains("IUnitOfWork.SaveChangesAsync", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_repository_implementations_reached_through_generic_repository_contracts()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.EntityFrameworkCore;
            using TCJ.Core.Entities;
            using TCJ.EntityFrameworkCore.Repositories;

            namespace Sample;

            public sealed class Order : Entity<long>
            {
            }

            public interface IRepositoryContract<TEntity> : IRepository<TEntity>
                where TEntity : class, IEntity<long>
            {
            }

            public interface IOrderRepository : IRepositoryContract<Order>
            {
            }

            public sealed class OrderRepository : EfRepository<Order>, IOrderRepository
            {
                private readonly DbContext _dbContext;

                public OrderRepository(
                    IReadRepository<Order, long> readRepository,
                    IWriteRepository<Order, long> writeRepository,
                    DbContext dbContext)
                    : base(readRepository, writeRepository)
                {
                    _dbContext = dbContext;
                }

                public Task<int> CommitAsync(CancellationToken cancellationToken) =>
                    _dbContext.SaveChangesAsync(cancellationToken);
            }
            """;

        Diagnostic diagnostic = Assert.Single(await GetDiagnosticsAsync(source));

        Assert.Equal("SaveChangesAsync", GetDiagnosticText(source, diagnostic));
    }

    [Fact]
    public async Task Does_not_report_application_owned_commits()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.EntityFrameworkCore;
            using TCJ.EntityFrameworkCore.UnitOfWork;

            namespace Sample;

            public sealed class OrderApplicationService
            {
                private readonly DbContext _dbContext;
                private readonly IUnitOfWork _unitOfWork;

                public OrderApplicationService(DbContext dbContext, IUnitOfWork unitOfWork)
                {
                    _dbContext = dbContext;
                    _unitOfWork = unitOfWork;
                }

                public async Task CommitAsync(CancellationToken cancellationToken)
                {
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                }
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Does_not_report_EfUnitOfWork_style_implementations()
    {
        const string source = """
            using System.Data;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.EntityFrameworkCore;
            using TCJ.EntityFrameworkCore.UnitOfWork;

            namespace Sample;

            public sealed class EfStyleUnitOfWork : IUnitOfWork
            {
                private readonly DbContext _dbContext;

                public EfStyleUnitOfWork(DbContext dbContext)
                {
                    _dbContext = dbContext;
                }

                public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
                    _dbContext.SaveChangesAsync(cancellationToken);

                public Task<IUnitOfWorkTransaction> BeginTransactionAsync(
                    CancellationToken cancellationToken = default) =>
                    throw new System.NotSupportedException();

                public Task<IUnitOfWorkTransaction> BeginTransactionAsync(
                    IsolationLevel isolationLevel,
                    CancellationToken cancellationToken = default) =>
                    throw new System.NotSupportedException();
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Does_not_report_method_name_lookalikes_or_extension_methods()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.EntityFrameworkCore;
            using TCJ.EntityFrameworkCore.Repositories;

            namespace Sample;

            public sealed class CommitLookalike
            {
                public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
                    Task.FromResult(0);
            }

            public static class DbContextExtensions
            {
                public static Task<int> SaveChangesAsync(this DbContext dbContext, string reason) =>
                    Task.FromResult(0);
            }

            public sealed class OrderRepository : IRepository
            {
                private readonly CommitLookalike _lookalike;
                private readonly DbContext _dbContext;

                public OrderRepository(CommitLookalike lookalike, DbContext dbContext)
                {
                    _lookalike = lookalike;
                    _dbContext = dbContext;
                }

                public async Task CommitAsync(CancellationToken cancellationToken)
                {
                    await _lookalike.SaveChangesAsync(cancellationToken);
                    await _dbContext.SaveChangesAsync("audit");
                }
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public async Task Does_not_report_abstract_repository_base_types()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.EntityFrameworkCore;
            using TCJ.EntityFrameworkCore.Repositories;

            namespace Sample;

            public abstract class RepositoryBase : IRepository
            {
                private readonly DbContext _dbContext;

                protected RepositoryBase(DbContext dbContext)
                {
                    _dbContext = dbContext;
                }

                protected Task<int> CommitAsync(CancellationToken cancellationToken) =>
                    _dbContext.SaveChangesAsync(cancellationToken);
            }
            """;

        Assert.Empty(await GetDiagnosticsAsync(source));
    }

    [Fact]
    public void Analyzer_project_does_not_reference_EntityFrameworkCore_runtime_assemblies()
    {
        XDocument project = XDocument.Load(
            RepositoryLayout.Combine("src/TCJ.Analyzers/TCJ.Analyzers.csproj"));

        string[] packageReferences = project.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();

        Assert.DoesNotContain(
            packageReferences,
            reference => reference.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void No_code_fix_provider_claims_TCJ1000()
    {
        Type[] providerTypes = typeof(ConflictingDependencyLifetimeMarkersCodeFixProvider).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(CodeFixProvider).IsAssignableFrom(type))
            .ToArray();

        foreach (Type providerType in providerTypes)
        {
            CodeFixProvider provider = Assert.IsAssignableFrom<CodeFixProvider>(
                Activator.CreateInstance(providerType));

            Assert.DoesNotContain("TCJ1000", provider.FixableDiagnosticIds);
        }
    }

    private static Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(string source) =>
        AnalyzerTestHost.GetAnalyzerDiagnosticsAsync(
            source,
            new RepositoryPersistenceCommitAnalyzer(),
            TcjReferenceAssemblyPaths);

    private static string GetDiagnosticText(string source, Diagnostic diagnostic) =>
        source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length);
}
