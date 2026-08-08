using Microsoft.EntityFrameworkCore;
using TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Infrastructure;

namespace TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Migrations;

internal static class SqlServerIntegrationMigrationModel
{
    internal static void Build(ModelBuilder modelBuilder)
    {
        SqlServerTestDbContextModelBuilder.Build(modelBuilder);
    }
}
