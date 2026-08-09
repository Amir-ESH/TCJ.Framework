using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TCJ.Core.Diagnostics;
using TCJ.Core.Entities;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Repositories;
using TCJ.EntityFrameworkCore.UnitOfWork;

namespace TCJ.Observability.Tests;

public sealed class RepositoryAndUnitOfWorkTelemetryTests : IDisposable
{
    private const string SecretMarker = "TCJ_TEST_TOKEN_MARKER";

    public RepositoryAndUnitOfWorkTelemetryTests() => TcjTelemetry.ResetForTests();

    [Fact]
    public async Task Repository_and_commit_emit_logical_spans_without_entity_values()
    {
        using var collector = new ActivityCollector(TcjDiagnosticNames.Sources.EntityFrameworkCore);
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new TestDbContext(options);
        var writes = new EfWriteRepository<TestEntity, Guid>(db);
        var reads = new EfReadRepository<TestEntity, Guid>(db);
        var unitOfWork = new EfUnitOfWork(db);
        var entity = new TestEntity(Guid.NewGuid(), SecretMarker);

        await writes.AddAsync(entity, CancellationToken.None);
        int affected = await unitOfWork.SaveChangesAsync(CancellationToken.None);
        TestEntity? loaded = await reads.GetByIdAsync(entity.Id, CancellationToken.None);

        Assert.Equal(1, affected);
        Assert.NotNull(loaded);
        Assert.Contains(collector.Activities, activity => activity.OperationName == TcjDiagnosticNames.Activities.RepositoryAdd);
        Assert.Contains(collector.Activities, activity => activity.OperationName == TcjDiagnosticNames.Activities.RepositoryGet);
        Activity commit = Assert.Single(
            collector.Activities,
            activity => activity.OperationName == TcjDiagnosticNames.Activities.UnitOfWorkCommit);
        Assert.Equal(ActivityStatusCode.Ok, commit.Status);
        Assert.Equal(1, commit.TagObjects.First(tag => tag.Key == TcjDiagnosticNames.Tags.AffectedRows).Value);

        string tags = string.Join(
            '\n',
            collector.Activities.SelectMany(static activity => activity.TagObjects)
                .Select(static tag => $"{tag.Key}={tag.Value}"));
        Assert.DoesNotContain(entity.Id.ToString(), tags, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SecretMarker, tags, StringComparison.Ordinal);
        Assert.DoesNotContain("raw_sql", tags, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection_string", tags, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Transaction_begin_and_rollback_emit_successful_outcomes()
    {
        using var collector = new ActivityCollector(TcjDiagnosticNames.Sources.EntityFrameworkCore);
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        await using var db = new TestDbContext(options);
        var unitOfWork = new EfUnitOfWork(db);

        await using IUnitOfWorkTransaction transaction = await unitOfWork.BeginTransactionAsync();
        await transaction.RollbackAsync();

        Activity begin = Assert.Single(
            collector.Activities,
            activity => activity.OperationName == TcjDiagnosticNames.Activities.TransactionBegin);
        Activity rollback = Assert.Single(
            collector.Activities,
            activity => activity.OperationName == TcjDiagnosticNames.Activities.TransactionRollback);

        Assert.Equal(ActivityStatusCode.Ok, begin.Status);
        Assert.Equal(ActivityStatusCode.Ok, rollback.Status);
        Assert.Equal(
            TcjDiagnosticNames.Outcomes.Success,
            rollback.TagObjects.First(tag => tag.Key == TcjDiagnosticNames.Tags.TransactionOutcome).Value);
    }

    [Fact]
    public async Task Unit_of_work_failure_preserves_original_exception_and_records_error()
    {
        using var collector = new ActivityCollector(TcjDiagnosticNames.Sources.EntityFrameworkCore);
        var expected = new InvalidOperationException($"database failure {SecretMarker}");
        await using var db = new FailingDbContext(expected);
        var unitOfWork = new EfUnitOfWork(db);

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => unitOfWork.SaveChangesAsync(CancellationToken.None));

        Assert.Same(expected, actual);
        Activity commit = Assert.Single(
            collector.Activities,
            activity => activity.OperationName == TcjDiagnosticNames.Activities.UnitOfWorkCommit);
        Assert.Equal(ActivityStatusCode.Error, commit.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName,
            commit.TagObjects.First(tag => tag.Key == TcjDiagnosticNames.Tags.ExceptionType).Value);
        Assert.DoesNotContain(commit.TagObjects, tag => tag.Key == TcjDiagnosticNames.Tags.ExceptionMessage);
    }

    public void Dispose() => TcjTelemetry.ResetForTests();

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options)
        : DbContext(options), IReadDbContext, IWriteDbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestEntity>().HasKey(entity => entity.Id);
        }
    }

    private sealed class FailingDbContext : DbContext, IReadDbContext, IWriteDbContext
    {
        private readonly Exception _exception;

        public FailingDbContext(Exception exception)
            : base(new DbContextOptionsBuilder<FailingDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options)
        {
            _exception = exception;
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<int>(_exception);
    }

    private sealed class TestEntity(Guid id, string name) : Entity<Guid>(id)
    {
        public string Name { get; private set; } = name;
    }
}
