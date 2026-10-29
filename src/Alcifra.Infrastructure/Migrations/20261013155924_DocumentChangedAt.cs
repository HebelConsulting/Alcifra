using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alcifra.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DocumentChangedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ChangedAt",
                table: "Documents",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // Backfill (ADR 0925): the latest of what a reader could have seen change before the column existed: the
            // document's creation, its deletion, and its newest version. Renames, moves and chat from before cannot
            // be recovered; a copy taken after this migration starts from its own snapshot, so nothing is missed.
            migrationBuilder.Sql(
                """
                UPDATE "Documents"
                SET "ChangedAt" = GREATEST(
                    "CreatedAt",
                    COALESCE("DeletedAt", "CreatedAt"),
                    COALESCE((SELECT MAX(v."CreatedAt") FROM "DocumentVersions" AS v WHERE v."DocumentId" = "Documents"."Id"), "CreatedAt"))
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Documents_TenantId_ChangedAt_Id",
                table: "Documents",
                columns: new[] { "TenantId", "ChangedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Documents_TenantId_ChangedAt_Id",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "ChangedAt",
                table: "Documents");
        }
    }
}
