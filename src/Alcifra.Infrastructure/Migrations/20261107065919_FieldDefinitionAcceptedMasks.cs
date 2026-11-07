using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alcifra.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FieldDefinitionAcceptedMasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FieldDefinitionAcceptedMasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    MaskId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FieldDefinitionAcceptedMasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FieldDefinitionAcceptedMasks_FieldDefinitions_FieldDefiniti~",
                        column: x => x.FieldDefinitionId,
                        principalTable: "FieldDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FieldDefinitionAcceptedMasks_Masks_TenantId_MaskId",
                        columns: x => new { x.TenantId, x.MaskId },
                        principalTable: "Masks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FieldDefinitionAcceptedMasks_FieldDefinitionId",
                table: "FieldDefinitionAcceptedMasks",
                column: "FieldDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_FieldDefinitionAcceptedMasks_TenantId_FieldDefinitionId_Mas~",
                table: "FieldDefinitionAcceptedMasks",
                columns: new[] { "TenantId", "FieldDefinitionId", "MaskId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FieldDefinitionAcceptedMasks_TenantId_MaskId",
                table: "FieldDefinitionAcceptedMasks",
                columns: new[] { "TenantId", "MaskId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FieldDefinitionAcceptedMasks");
        }
    }
}
