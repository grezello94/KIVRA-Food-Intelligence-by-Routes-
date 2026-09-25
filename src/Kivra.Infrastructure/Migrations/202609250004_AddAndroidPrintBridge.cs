using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kivra.Infrastructure.Migrations;

[DbContext(typeof(KivraDbContext))]
[Migration("202609250004_AddAndroidPrintBridge")]
public sealed class AddAndroidPrintBridge : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(name: "BridgeDeviceId", table: "PrintJobs", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(name: "LeaseExpiresAt", table: "PrintJobs", type: "timestamp with time zone", nullable: true);

        migrationBuilder.CreateTable(
            name: "BridgeDevices",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                TokenHash = table.Column<string>(type: "text", nullable: false),
                Active = table.Column<bool>(type: "boolean", nullable: false),
                LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                AppVersion = table.Column<string>(type: "text", nullable: true),
                Platform = table.Column<string>(type: "text", nullable: true),
                RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_BridgeDevices", x => x.Id));

        migrationBuilder.CreateTable(
            name: "BridgePairingCodes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CodeHash = table.Column<string>(type: "text", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_BridgePairingCodes", x => x.Id));

        migrationBuilder.CreateIndex(name: "IX_BridgeDevices_TokenHash", table: "BridgeDevices", column: "TokenHash", unique: true);
        migrationBuilder.CreateIndex(name: "IX_BridgePairingCodes_CodeHash", table: "BridgePairingCodes", column: "CodeHash");
        migrationBuilder.CreateIndex(name: "IX_PrintJobs_Status_LeaseExpiresAt", table: "PrintJobs", columns: new[] { "Status", "LeaseExpiresAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "BridgePairingCodes");
        migrationBuilder.DropTable(name: "BridgeDevices");
        migrationBuilder.DropIndex(name: "IX_PrintJobs_Status_LeaseExpiresAt", table: "PrintJobs");
        migrationBuilder.DropColumn(name: "BridgeDeviceId", table: "PrintJobs");
        migrationBuilder.DropColumn(name: "LeaseExpiresAt", table: "PrintJobs");
    }
}
