using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TCJ.Core.Entities;
using TCJ.EntityFrameworkCore.Extensions;

namespace TCJ.EntityFrameworkCore.Tests;

public sealed class SoftDeleteQueryFilterTests
{
    [Fact]
    public async Task Preserves_existing_anonymous_query_filter()
    {
        DbContextOptions<AnonymousFilterDbContext> options = new DbContextOptionsBuilder<AnonymousFilterDbContext>()
                                                            .UseInMemoryDatabase(Guid.NewGuid().ToString(format: "N"))
                                                            .Options;

        await using var context = new AnonymousFilterDbContext(options);
        context.Entities.AddRange(
            new QueryFilterEntity { Id = 1, TenantId = 1 },
            new QueryFilterEntity { Id = 2, TenantId = 2 },
            new QueryFilterEntity { Id = 3, TenantId = 1, IsDeleted = true });
        await context.SaveChangesAsync(CancellationToken.None);

        QueryFilterEntity result = Assert.Single(await context.Entities.ToListAsync(CancellationToken.None));
        Assert.Equal(1, result.Id);

        IReadOnlyCollection<IQueryFilter> filters = context.Model.FindEntityType(typeof(QueryFilterEntity))!
                                                              .GetDeclaredQueryFilters();
        IQueryFilter filter = Assert.Single(filters);
        Assert.True(filter.IsAnonymous);
        Assert.NotNull(filter.Expression);
    }

    [Fact]
    public async Task Preserves_existing_named_query_filter()
    {
        DbContextOptions<NamedFilterDbContext> options = new DbContextOptionsBuilder<NamedFilterDbContext>()
                                                        .UseInMemoryDatabase(Guid.NewGuid().ToString(format: "N"))
                                                        .Options;

        await using var context = new NamedFilterDbContext(options);
        context.Entities.AddRange(
            new QueryFilterEntity { Id = 1, TenantId = 1 },
            new QueryFilterEntity { Id = 2, TenantId = 2 },
            new QueryFilterEntity { Id = 3, TenantId = 1, IsDeleted = true });
        await context.SaveChangesAsync(CancellationToken.None);

        QueryFilterEntity result = Assert.Single(await context.Entities.ToListAsync(CancellationToken.None));
        Assert.Equal(1, result.Id);

        IReadOnlyCollection<IQueryFilter> filters = context.Model.FindEntityType(typeof(QueryFilterEntity))!
                                                              .GetDeclaredQueryFilters();
        Assert.Equal(2, filters.Count);
        Assert.False(filters.Any(filter => filter.IsAnonymous));
        Assert.True(filters.Any(filter => filter.Key == "TenantFilter"));
        Assert.True(filters.Any(filter => filter.Key == "TCJ:SoftDelete"));

        int[] includingSoftDeleted = await context.Entities
                                                   .IgnoreQueryFilters(["TCJ:SoftDelete"])
                                                   .OrderBy(entity => entity.Id)
                                                   .Select(entity => entity.Id)
                                                   .ToArrayAsync(CancellationToken.None);
        Assert.Equal(new[] { 1, 3 }, includingSoftDeleted);

        int[] acrossTenants = await context.Entities
                                           .IgnoreQueryFilters(["TenantFilter"])
                                           .OrderBy(entity => entity.Id)
                                           .Select(entity => entity.Id)
                                           .ToArrayAsync(CancellationToken.None);
        Assert.Equal(new[] { 1, 2 }, acrossTenants);
    }

    private sealed class AnonymousFilterDbContext(DbContextOptions<AnonymousFilterDbContext> options) : DbContext(options)
    {
        public DbSet<QueryFilterEntity> Entities => Set<QueryFilterEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<QueryFilterEntity>().HasQueryFilter(entity => entity.TenantId == 1);
            modelBuilder.ApplySoftDeleteQueryFilters();
        }
    }

    private sealed class NamedFilterDbContext(DbContextOptions<NamedFilterDbContext> options) : DbContext(options)
    {
        public DbSet<QueryFilterEntity> Entities => Set<QueryFilterEntity>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<QueryFilterEntity>().HasQueryFilter("TenantFilter", entity => entity.TenantId == 1);
            modelBuilder.ApplySoftDeleteQueryFilters();
        }
    }

    private sealed class QueryFilterEntity : ISoftDelete
    {
        public int Id { get; set; }

        public int TenantId { get; set; }

        public bool IsDeleted { get; set; }

        public DateTimeOffset? DeletedOn { get; set; }

        public long? DeletedBy { get; set; }
    }
}
