using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TCJ.HealthChecks.Tests.Infrastructure;

namespace TCJ.HealthChecks.Tests.Migrations;

[DbContext(typeof(HealthTestDbContext))]
[Migration("202608100001_HealthCheckPending")]
internal sealed class PendingHealthMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.CreateTable(
            name: "HealthPendingRows",
            columns: table => new { Id = table.Column<int>(nullable: false) },
            constraints: table => table.PrimaryKey("PK_HealthPendingRows", x => x.Id));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("HealthPendingRows");
}
