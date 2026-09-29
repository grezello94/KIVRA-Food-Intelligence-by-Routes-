using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kivra.Infrastructure.Migrations;

[DbContext(typeof(KivraDbContext))]
[Migration("202609290005_PreventDuplicateItemNames")]
public sealed class PreventDuplicateItemNames : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Items_NormalizedName\" ON \"Items\" (lower(btrim(\"Name\")));");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Items_NormalizedName\";");
}
