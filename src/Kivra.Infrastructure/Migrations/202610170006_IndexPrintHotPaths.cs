using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kivra.Infrastructure.Migrations;

[DbContext(typeof(KivraDbContext))]
[Migration("202610170006_IndexPrintHotPaths")]
public partial class IndexPrintHotPaths : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_Labels_OperationalDateTime",
            table: "Labels",
            column: "OperationalDateTime");
        migrationBuilder.CreateIndex(
            name: "IX_PrintJobs_Status_RequestedAt",
            table: "PrintJobs",
            columns: new[] { "Status", "RequestedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Labels_OperationalDateTime", table: "Labels");
        migrationBuilder.DropIndex(name: "IX_PrintJobs_Status_RequestedAt", table: "PrintJobs");
    }
}
