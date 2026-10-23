using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AttestedCertificateEnrolment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CertificateUnattestedHardwareAllowed",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AttestationRootThumbprint",
                table: "EnrolledCertificates",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CertificateAttestations",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AttestationCertificatePem = table.Column<string>(type: "text", nullable: false),
                    IntermediateCertificatePem = table.Column<string>(type: "text", nullable: false),
                    DeviceSerial = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FirmwareVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Slot = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CertificateAttestations", x => x.DocumentId);
                    table.ForeignKey(
                        name: "FK_CertificateAttestations_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CertificateAttestations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TrustedAttestationRoots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CertificatePem = table.Column<string>(type: "text", nullable: false),
                    Thumbprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    CatalogueKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrustedAttestationRoots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantDisabledAttestationRoots",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RootId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantDisabledAttestationRoots", x => new { x.TenantId, x.RootId });
                    table.ForeignKey(
                        name: "FK_TenantDisabledAttestationRoots_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TenantDisabledAttestationRoots_TrustedAttestationRoots_Root~",
                        column: x => x.RootId,
                        principalTable: "TrustedAttestationRoots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CertificateAttestations_TenantId",
                table: "CertificateAttestations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantDisabledAttestationRoots_RootId",
                table: "TenantDisabledAttestationRoots",
                column: "RootId");

            migrationBuilder.CreateIndex(
                name: "IX_TrustedAttestationRoots_CatalogueKey",
                table: "TrustedAttestationRoots",
                column: "CatalogueKey",
                unique: true,
                filter: "\"CatalogueKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TrustedAttestationRoots_Thumbprint",
                table: "TrustedAttestationRoots",
                column: "Thumbprint",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CertificateAttestations");

            migrationBuilder.DropTable(
                name: "TenantDisabledAttestationRoots");

            migrationBuilder.DropTable(
                name: "TrustedAttestationRoots");

            migrationBuilder.DropColumn(
                name: "CertificateUnattestedHardwareAllowed",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AttestationRootThumbprint",
                table: "EnrolledCertificates");
        }
    }
}
