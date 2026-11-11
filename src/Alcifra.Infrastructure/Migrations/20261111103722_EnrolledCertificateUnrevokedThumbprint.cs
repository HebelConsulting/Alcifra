using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alcifra.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnrolledCertificateUnrevokedThumbprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EnrolledCertificates_TenantId_Thumbprint",
                table: "EnrolledCertificates");

            migrationBuilder.CreateIndex(
                name: "IX_EnrolledCertificates_TenantId_Thumbprint",
                table: "EnrolledCertificates",
                columns: new[] { "TenantId", "Thumbprint" },
                unique: true,
                filter: "\"RevokedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EnrolledCertificates_TenantId_Thumbprint",
                table: "EnrolledCertificates");

            migrationBuilder.CreateIndex(
                name: "IX_EnrolledCertificates_TenantId_Thumbprint",
                table: "EnrolledCertificates",
                columns: new[] { "TenantId", "Thumbprint" },
                unique: true);
        }
    }
}
