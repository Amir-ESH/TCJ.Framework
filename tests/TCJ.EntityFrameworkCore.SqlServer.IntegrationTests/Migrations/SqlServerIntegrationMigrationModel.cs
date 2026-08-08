using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;
using TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Infrastructure;

namespace TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Migrations;

internal static class SqlServerIntegrationMigrationModel
{
    internal static void Build(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasAnnotation("ProductVersion", "10.0.10")
            .HasAnnotation("Relational:MaxIdentifierLength", 128)
            .HasAnnotation("SqlServer:ValueGenerationStrategy", SqlServerValueGenerationStrategy.IdentityColumn);

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
                  .UseIdentityColumn()
                  .HasColumnType("int");
            entity.Property(value => value.Name)
                  .HasColumnType("nvarchar(80)")
                  .HasMaxLength(80)
                  .IsRequired();
            entity.Property(value => value.ParentId).HasColumnType("int");
            entity.HasOne(value => value.Parent)
                  .WithMany(value => value.Children)
                  .HasForeignKey(value => value.ParentId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.ApplySoftDeleteQueryFilters();
        modelBuilder.ApplyTcjSqlServerConventions();
    }
}
