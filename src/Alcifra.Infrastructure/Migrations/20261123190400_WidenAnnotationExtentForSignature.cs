using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alcifra.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WidenAnnotationExtentForSignature : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DocumentAnnotations_FreehandPoints",
                table: "DocumentAnnotations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DocumentAnnotations_ShapeExtent",
                table: "DocumentAnnotations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DocumentAnnotations_FreehandPoints",
                table: "DocumentAnnotations",
                sql: "(\"Kind\" IN (7, 8) AND \"Points\" IS NOT NULL) OR (\"Kind\" NOT IN (7, 8) AND \"Points\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DocumentAnnotations_ShapeExtent",
                table: "DocumentAnnotations",
                sql: "\"Kind\" IN (0, 7, 8) OR (\"Width\" IS NOT NULL AND \"Height\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DocumentAnnotations_FreehandPoints",
                table: "DocumentAnnotations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DocumentAnnotations_ShapeExtent",
                table: "DocumentAnnotations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DocumentAnnotations_FreehandPoints",
                table: "DocumentAnnotations",
                sql: "(\"Kind\" = 7 AND \"Points\" IS NOT NULL) OR (\"Kind\" <> 7 AND \"Points\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DocumentAnnotations_ShapeExtent",
                table: "DocumentAnnotations",
                sql: "\"Kind\" IN (0, 7) OR (\"Width\" IS NOT NULL AND \"Height\" IS NOT NULL)");
        }
    }
}
