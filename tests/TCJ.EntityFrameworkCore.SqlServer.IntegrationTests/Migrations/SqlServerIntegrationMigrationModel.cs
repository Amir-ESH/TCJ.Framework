using Microsoft.EntityFrameworkCore;
using TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Infrastructure;

namespace TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Migrations;

internal static class SqlServerIntegrationMigrationModel
{
    internal static void Build(ModelBuilder modelBuilder)
    {
        // Match the provider metadata emitted by an EF-generated SQL Server
        // snapshot before applying the same explicit relational mappings used
        // by the runtime test context.
        modelBuilder
            .HasAnnotation("ProductVersion", "10.0.10")
            .HasAnnotation("Relational:MaxIdentifierLength", 128);
        modelBuilder.UseIdentityColumns();

        SqlServerTestDbContextModelBuilder.Build(modelBuilder);
    }
}
