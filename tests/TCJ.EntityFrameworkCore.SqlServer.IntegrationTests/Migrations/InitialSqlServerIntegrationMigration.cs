using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Migrations;

internal sealed partial class InitialSqlServerIntegrationMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "IntegrationEntities",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                Amount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                OccurredOn = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                OptionalText = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                CreatedOn = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                ModifiedOn = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                CreatedBy = table.Column<long>(type: "bigint", nullable: true),
                ModifiedBy = table.Column<long>(type: "bigint", nullable: true),
                IsDeleted = table.Column<bool>(type: "bit", nullable: false),
                DeletedOn = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                DeletedBy = table.Column<long>(type: "bigint", nullable: true),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IntegrationEntities", value => value.Id);
            });

        migrationBuilder.CreateTable(
            name: "IntegrationParents",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IntegrationParents", value => value.Id);
            });

        migrationBuilder.CreateTable(
            name: "IntegrationChildren",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Name = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                ParentId = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_IntegrationChildren", value => value.Id);
                table.ForeignKey(
                    name: "FK_IntegrationChildren_IntegrationParents_ParentId",
                    column: value => value.ParentId,
                    principalTable: "IntegrationParents",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_IntegrationChildren_ParentId",
            table: "IntegrationChildren",
            column: "ParentId");

        migrationBuilder.CreateIndex(
            name: "IX_IntegrationEntities_Name",
            table: "IntegrationEntities",
            column: "Name",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "IntegrationChildren");
        migrationBuilder.DropTable(name: "IntegrationEntities");
        migrationBuilder.DropTable(name: "IntegrationParents");
    }
}
