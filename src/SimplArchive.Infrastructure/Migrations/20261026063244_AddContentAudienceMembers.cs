using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddContentAudienceMembers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContentAudienceMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsManager = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentAudienceMembers", x => x.Id);
                    table.CheckConstraint("CK_ContentAudienceMembers_ExactlyOnePrincipal", "(CASE WHEN \"UserId\" IS NOT NULL THEN 1 ELSE 0 END + CASE WHEN \"GroupId\" IS NOT NULL THEN 1 ELSE 0 END) = 1");
                    table.CheckConstraint("CK_ContentAudienceMembers_ManagerIsAUser", "NOT \"IsManager\" OR \"UserId\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_ContentAudienceMembers_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContentAudienceMembers_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContentAudienceMembers_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContentAudienceMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContentAudienceMembers_DocumentId",
                table: "ContentAudienceMembers",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentAudienceMembers_GroupId",
                table: "ContentAudienceMembers",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentAudienceMembers_TenantId_DocumentId_GroupId",
                table: "ContentAudienceMembers",
                columns: new[] { "TenantId", "DocumentId", "GroupId" },
                unique: true,
                filter: "\"GroupId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ContentAudienceMembers_TenantId_DocumentId_UserId",
                table: "ContentAudienceMembers",
                columns: new[] { "TenantId", "DocumentId", "UserId" },
                unique: true,
                filter: "\"UserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ContentAudienceMembers_UserId",
                table: "ContentAudienceMembers",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContentAudienceMembers");
        }
    }
}
