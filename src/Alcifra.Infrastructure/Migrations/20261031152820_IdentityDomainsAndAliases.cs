using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alcifra.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class IdentityDomainsAndAliases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenantMailDomains_NormalizedDomain",
                table: "TenantMailDomains");

            migrationBuilder.DropIndex(
                name: "IX_TenantMailDomains_TenantId",
                table: "TenantMailDomains");

            migrationBuilder.AddColumn<Guid>(
                name: "ConcurrencyToken",
                table: "TenantMailDomains",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Every existing domain becomes BOTH roles, which is what it was before the roles existed (ADR 1015): nothing
            // changes for an existing tenant, and the receiving rows keep the global uniqueness they had.
            migrationBuilder.AddColumn<bool>(
                name: "IsIdentity",
                table: "TenantMailDomains",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsReceiving",
                table: "TenantMailDomains",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "UserIdentityAliases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    NormalizedAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserIdentityAliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserIdentityAliases_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantMailDomains_NormalizedDomain",
                table: "TenantMailDomains",
                column: "NormalizedDomain",
                unique: true,
                filter: "\"IsReceiving\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_TenantMailDomains_TenantId_NormalizedDomain",
                table: "TenantMailDomains",
                columns: new[] { "TenantId", "NormalizedDomain" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserIdentityAliases_TenantId_NormalizedAddress",
                table: "UserIdentityAliases",
                columns: new[] { "TenantId", "NormalizedAddress" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserIdentityAliases_UserId",
                table: "UserIdentityAliases",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserIdentityAliases");

            migrationBuilder.DropIndex(
                name: "IX_TenantMailDomains_NormalizedDomain",
                table: "TenantMailDomains");

            migrationBuilder.DropIndex(
                name: "IX_TenantMailDomains_TenantId_NormalizedDomain",
                table: "TenantMailDomains");

            migrationBuilder.DropColumn(
                name: "ConcurrencyToken",
                table: "TenantMailDomains");

            migrationBuilder.DropColumn(
                name: "IsIdentity",
                table: "TenantMailDomains");

            migrationBuilder.DropColumn(
                name: "IsReceiving",
                table: "TenantMailDomains");

            migrationBuilder.CreateIndex(
                name: "IX_TenantMailDomains_NormalizedDomain",
                table: "TenantMailDomains",
                column: "NormalizedDomain",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantMailDomains_TenantId",
                table: "TenantMailDomains",
                column: "TenantId");
        }
    }
}
