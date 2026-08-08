using Microsoft.EntityFrameworkCore;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;

namespace TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Infrastructure;

internal static class SqlServerTestDbContextModelBuilder
{
    internal static void Build(ModelBuilder modelBuilder)
    {
        // ModelSnapshot uses a reduced convention set, so properties inherited
        // from TCJ entity base classes must be mapped explicitly. Keeping every
        // relational facet here also makes the runtime model and migration
        // snapshot execute the same configuration instead of relying on two
        // different convention pipelines.
        modelBuilder.UseIdentityColumns();

        modelBuilder.Entity<SqlServerTestEntity>(entity =>
        {
            entity.ToTable("IntegrationEntities");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id)
                  .ValueGeneratedOnAdd()
                  .HasColumnType("uniqueidentifier");
            entity.Property(value => value.Name)
                  .HasColumnType("nvarchar(80)")
                  .HasMaxLength(80)
                  .IsRequired();
            entity.HasIndex(value => value.Name).IsUnique();
            entity.Property(value => value.Amount)
                  .HasColumnType("decimal(18,4)")
                  .HasPrecision(18, 4);
            entity.Property(value => value.OccurredOn).HasColumnType("datetimeoffset(7)");
            entity.Property(value => value.OptionalText)
                  .HasColumnType("nvarchar(100)")
                  .HasMaxLength(100);
            entity.Property(value => value.CreatedOn).HasColumnType("datetimeoffset");
            entity.Property(value => value.ModifiedOn).HasColumnType("datetimeoffset");
            entity.Property(value => value.CreatedBy).HasColumnType("bigint");
            entity.Property(value => value.ModifiedBy).HasColumnType("bigint");
            entity.Property(value => value.IsDeleted).HasColumnType("bit");
            entity.Property(value => value.DeletedOn).HasColumnType("datetimeoffset");
            entity.Property(value => value.DeletedBy).HasColumnType("bigint");
            entity.Property(value => value.RowVersion)
                  .IsRequired()
                  .IsRowVersion()
                  .HasColumnType("rowversion");
        });

        modelBuilder.Entity<SqlServerParent>(entity =>
        {
            entity.ToTable("IntegrationParents");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id)
                  .ValueGeneratedOnAdd()
                  .UseIdentityColumn()
                  .HasColumnType("int");
            entity.Property(value => value.Name)
                  .HasColumnType("nvarchar(80)")
                  .HasMaxLength(80)
                  .IsRequired();
        });

        modelBuilder.Entity<SqlServerChild>(entity =>
        {
            entity.ToTable("IntegrationChildren");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id)
                  .ValueGeneratedOnAdd()
                  .UseIdentityColumn()
                  .HasColumnType("int");
            entity.Property(value => value.Name)
                  .HasColumnType("nvarchar(80)")
                  .HasMaxLength(80)
                  .IsRequired();
            entity.Property(value => value.ParentId).HasColumnType("int");
            entity.HasIndex(value => value.ParentId);
            entity.HasOne(value => value.Parent)
                  .WithMany(value => value.Children)
                  .HasForeignKey(value => value.ParentId)
                  .IsRequired()
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.ApplySoftDeleteQueryFilters();
        modelBuilder.ApplyTcjSqlServerConventions();
    }
}
