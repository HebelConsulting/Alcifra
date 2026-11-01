using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alcifra.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProtocolDeviceHandOvers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProtocolDeviceHandOvers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SealedProfile = table.Column<byte[]>(type: "bytea", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProtocolDeviceHandOvers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProtocolDeviceHandOvers_ProtocolDevices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "ProtocolDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProtocolDeviceHandOvers_DeviceId",
                table: "ProtocolDeviceHandOvers",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_ProtocolDeviceHandOvers_ExpiresAt",
                table: "ProtocolDeviceHandOvers",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ProtocolDeviceHandOvers_TenantId",
                table: "ProtocolDeviceHandOvers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ProtocolDeviceHandOvers_TokenHash",
                table: "ProtocolDeviceHandOvers",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProtocolDeviceHandOvers");
        }
    }
}
