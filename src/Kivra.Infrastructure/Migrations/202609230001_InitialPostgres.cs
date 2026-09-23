using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kivra.Infrastructure.Migrations;

/// <summary>Creates the PostgreSQL schema used by KIVRA.</summary>
[DbContext(typeof(KivraDbContext))]
[Migration("202609230001_InitialPostgres")]
public partial class InitialPostgres : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "Categories" ("Id" uuid NOT NULL PRIMARY KEY, "Name" text NOT NULL, "Active" boolean NOT NULL, "CreatedAt" timestamp with time zone NOT NULL, "UpdatedAt" timestamp with time zone NOT NULL);
            CREATE TABLE "StorageLocations" ("Id" uuid NOT NULL PRIMARY KEY, "Name" text NOT NULL, "Active" boolean NOT NULL, "CreatedAt" timestamp with time zone NOT NULL, "UpdatedAt" timestamp with time zone NOT NULL);
            CREATE TABLE "Users" ("Id" uuid NOT NULL PRIMARY KEY, "DisplayName" text NOT NULL, "PinHash" text NOT NULL, "Role" integer NOT NULL, "Active" boolean NOT NULL, "CreatedAt" timestamp with time zone NOT NULL, "UpdatedAt" timestamp with time zone NOT NULL);
            CREATE TABLE "Printers" ("Id" uuid NOT NULL PRIMARY KEY, "Name" text NOT NULL, "Model" text NOT NULL, "Driver" text NOT NULL, "IpAddress" text NOT NULL, "TcpPort" integer NOT NULL, "UsbQueueName" text NULL, "Dpi" integer NOT NULL, "LabelWidthMm" numeric NOT NULL, "LabelHeightMm" numeric NOT NULL, "MediaSensingMode" text NOT NULL, "GapMm" numeric NOT NULL, "PrintMethod" text NOT NULL, "PrintSpeedIps" numeric NOT NULL, "PrintDensity" integer NOT NULL, "Enabled" boolean NOT NULL, "CreatedAt" timestamp with time zone NOT NULL, "UpdatedAt" timestamp with time zone NOT NULL);
            CREATE TABLE "Items" ("Id" uuid NOT NULL PRIMARY KEY, "Name" text NOT NULL, "ShortName" text NULL, "CategoryId" uuid NOT NULL REFERENCES "Categories"("Id") ON DELETE CASCADE, "Classification" integer NOT NULL, "LabelType" text NOT NULL, "DateTerminology" text NOT NULL, "ShelfLifeValue" integer NOT NULL, "ShelfLifeUnit" integer NOT NULL, "DefaultStorageLocationId" uuid NULL REFERENCES "StorageLocations"("Id"), "DefaultLabelTemplate" text NOT NULL, "Active" boolean NOT NULL, "CreatedAt" timestamp with time zone NOT NULL, "UpdatedAt" timestamp with time zone NOT NULL);
            CREATE TABLE "Labels" ("Id" uuid NOT NULL PRIMARY KEY, "LabelCode" text NOT NULL, "ItemId" uuid NOT NULL, "ItemNameSnapshot" text NOT NULL, "CategorySnapshot" text NOT NULL, "ClassificationSnapshot" integer NOT NULL, "DateTerminologySnapshot" text NOT NULL, "OperationalDateTime" timestamp with time zone NOT NULL, "ExpiryDateTime" timestamp with time zone NOT NULL, "ShelfLifeRuleSnapshot" character varying(100) NOT NULL, "StorageLocationId" uuid NULL, "StorageLocationSnapshot" text NOT NULL, "Quantity" numeric NULL, "Unit" text NULL, "CreatedByUserId" uuid NOT NULL, "CurrentStatus" integer NOT NULL, "PrinterId" uuid NOT NULL, "LastSuccessfulPrintAt" timestamp with time zone NULL, "SuccessfulPrintCount" integer NOT NULL, "CreatedAt" timestamp with time zone NOT NULL, "UpdatedAt" timestamp with time zone NOT NULL);
            CREATE TABLE "AuditLogs" ("Id" uuid NOT NULL PRIMARY KEY, "UserId" uuid NULL, "Action" text NOT NULL, "EntityName" text NOT NULL, "EntityId" uuid NOT NULL, "PreviousValues" text NULL, "NewValues" text NULL, "DeviceIp" text NULL, "CreatedAt" timestamp with time zone NOT NULL, "UpdatedAt" timestamp with time zone NOT NULL);
            CREATE TABLE "Notifications" ("Id" uuid NOT NULL PRIMARY KEY, "Type" text NOT NULL, "Title" text NOT NULL, "Message" text NOT NULL, "LabelId" uuid NULL, "ReadAt" timestamp with time zone NULL, "CreatedAt" timestamp with time zone NOT NULL, "UpdatedAt" timestamp with time zone NOT NULL);
            CREATE TABLE "PrintJobs" ("Id" uuid NOT NULL PRIMARY KEY, "LabelId" uuid NOT NULL REFERENCES "Labels"("Id") ON DELETE CASCADE, "PrinterId" uuid NOT NULL, "RequestedByUserId" uuid NOT NULL, "RequestedAt" timestamp with time zone NOT NULL, "StartedAt" timestamp with time zone NULL, "CompletedAt" timestamp with time zone NULL, "Status" integer NOT NULL, "FailureReason" text NULL, "RetryCount" integer NOT NULL, "IsReprint" boolean NOT NULL, "Payload" text NULL, "IdempotencyKey" text NULL, "CreatedAt" timestamp with time zone NOT NULL, "UpdatedAt" timestamp with time zone NOT NULL);
            CREATE UNIQUE INDEX "IX_Items_Name" ON "Items" ("Name");
            CREATE INDEX "IX_Items_CategoryId" ON "Items" ("CategoryId");
            CREATE INDEX "IX_Items_DefaultStorageLocationId" ON "Items" ("DefaultStorageLocationId");
            CREATE UNIQUE INDEX "IX_Labels_LabelCode" ON "Labels" ("LabelCode");
            CREATE INDEX "IX_Labels_ExpiryDateTime" ON "Labels" ("ExpiryDateTime");
            CREATE INDEX "IX_Labels_CurrentStatus" ON "Labels" ("CurrentStatus");
            CREATE INDEX "IX_Labels_ItemId" ON "Labels" ("ItemId");
            CREATE INDEX "IX_Labels_CreatedAt" ON "Labels" ("CreatedAt");
            CREATE INDEX "IX_PrintJobs_LabelId" ON "PrintJobs" ("LabelId");
            CREATE UNIQUE INDEX "IX_PrintJobs_IdempotencyKey" ON "PrintJobs" ("IdempotencyKey") WHERE "IdempotencyKey" IS NOT NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE "PrintJobs";
            DROP TABLE "Notifications";
            DROP TABLE "AuditLogs";
            DROP TABLE "Labels";
            DROP TABLE "Items";
            DROP TABLE "Printers";
            DROP TABLE "Users";
            DROP TABLE "StorageLocations";
            DROP TABLE "Categories";
            """);
    }
}
