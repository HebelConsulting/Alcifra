using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alcifra.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DeviceChosenCollections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CollectionsChosen",
                table: "ProtocolDevices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ProtocolDeviceCollections",
                columns: table => new
                {
                    ProtocolDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    FolderId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProtocolDeviceCollections", x => new { x.ProtocolDeviceId, x.FolderId });
                    table.ForeignKey(
                        name: "FK_ProtocolDeviceCollections_Documents_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProtocolDeviceCollections_ProtocolDevices_ProtocolDeviceId",
                        column: x => x.ProtocolDeviceId,
                        principalTable: "ProtocolDevices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProtocolDeviceCollections_FolderId",
                table: "ProtocolDeviceCollections",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_ProtocolDeviceCollections_TenantId",
                table: "ProtocolDeviceCollections",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProtocolDeviceCollections");

            migrationBuilder.DropColumn(
                name: "CollectionsChosen",
                table: "ProtocolDevices");
        }
    }
}
