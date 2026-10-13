using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOfflineCachePolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OfflineCacheAllowed",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "OfflineCacheChosenFolders",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "OfflineCacheMaxUnlockedMinutes",
                table: "Tenants",
                type: "integer",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.AddColumn<bool>(
                name: "OfflineCacheNotifications",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "OfflineCacheOpenedDocuments",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "OfflineCacheRefreshMode",
                table: "Tenants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "OfflineCacheUnlockMode",
                table: "Tenants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tenants_OfflineCacheMaxUnlockedMinutes",
                table: "Tenants",
                sql: "\"OfflineCacheMaxUnlockedMinutes\" BETWEEN 1 AND 1440");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tenants_OfflineCacheRefreshMode",
                table: "Tenants",
                sql: "\"OfflineCacheRefreshMode\" IN (0, 1, 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Tenants_OfflineCacheUnlockMode",
                table: "Tenants",
                sql: "\"OfflineCacheUnlockMode\" IN (0, 1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Tenants_OfflineCacheMaxUnlockedMinutes",
                table: "Tenants");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tenants_OfflineCacheRefreshMode",
                table: "Tenants");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Tenants_OfflineCacheUnlockMode",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "OfflineCacheAllowed",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "OfflineCacheChosenFolders",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "OfflineCacheMaxUnlockedMinutes",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "OfflineCacheNotifications",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "OfflineCacheOpenedDocuments",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "OfflineCacheRefreshMode",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "OfflineCacheUnlockMode",
                table: "Tenants");
        }
    }
}
