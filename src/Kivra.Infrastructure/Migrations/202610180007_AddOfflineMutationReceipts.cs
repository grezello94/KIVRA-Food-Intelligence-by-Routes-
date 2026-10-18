using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kivra.Infrastructure.Migrations;

[DbContext(typeof(KivraDbContext))]
[Migration("202610180007_AddOfflineMutationReceipts")]
public sealed class AddOfflineMutationReceipts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OfflineMutationReceipts",
            columns: table => new
            {
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                StatusCode = table.Column<int>(type: "integer", nullable: false),
                ContentType = table.Column<string>(type: "text", nullable: true),
                Location = table.Column<string>(type: "text", nullable: true),
                ResponseBody = table.Column<byte[]>(type: "bytea", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_OfflineMutationReceipts", x => new { x.UserId, x.IdempotencyKey }));
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("OfflineMutationReceipts");
}
