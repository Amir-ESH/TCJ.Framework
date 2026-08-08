using Microsoft.EntityFrameworkCore;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;

namespace TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Infrastructure;

internal static class SqlServerTestDbContextModelBuilder
{
    internal static void Build(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SqlServerTestEntity>(entity =>
        {
            entity.ToTable("IntegrationEntities");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Name).HasMaxLength(80).IsRequired();
            entity.HasIndex(value => value.Name).IsUnique();
            entity.Property(value => value.Amount).HasPrecision(18, 4);
            entity.Property(value => value.OccurredOn).HasColumnType("datetimeoffset(7)");
            entity.Property(value => value.OptionalText).HasMaxLength(100);
        });

        modelBuilder.Entity<SqlServerParent>(entity =>
        {
            entity.ToTable("IntegrationParents");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id).ValueGeneratedOnAdd();
            entity.Property(value => value.Name).HasMaxLength(80).IsRequired();
        });

        modelBuilder.Entity<SqlServerChild>(entity =>
        {
            entity.ToTable("IntegrationChildren");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id).ValueGeneratedOnAdd();
            entity.Property(value => value.Name).HasMaxLength(80).IsRequired();
            entity.HasOne(value => value.Parent)
                  .WithMany(value => value.Children)
                  .HasForeignKey(value => value.ParentId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.ApplySoftDeleteQueryFilters();
        modelBuilder.ApplyTcjSqlServerConventions();
    }
}
