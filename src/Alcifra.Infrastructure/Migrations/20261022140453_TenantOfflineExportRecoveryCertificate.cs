using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alcifra.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TenantOfflineExportRecoveryCertificate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OfflineExportRecoveryCertificatePem",
                table: "Tenants",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OfflineExportRecoveryCertificatePem",
                table: "Tenants");
        }
    }
}
