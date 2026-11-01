using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alcifra.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropRetiredUserColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The two password hashes are safe to drop: ProtocolDevices (v0.56.0) copied each one into an "earlier"
            // device in the same migration run, whichever release an installation comes from (ADR 1020).
            //
            // The certificate column is not: v0.56.0's STARTUP carried users' own certificates into the registry, so an
            // installation that never started v0.56.0, or one whose tenant had no folder for reader certificates chosen,
            // still holds certificates that exist nowhere else (#2059, owner 2026-11-01). REFUSE rather than lose them.
            // A user is accounted for when the carry filed their certificate (a registry row of theirs, revoked or not)
            // or told them it no longer addresses them (the OwnCertificateRetired notification, type 22).
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                migrationBuilder.Sql(
                    """
                    DO $$
                    DECLARE uncarried bigint;
                    BEGIN
                        SELECT count(*) INTO uncarried FROM "Users" u
                        WHERE u."IsActive"
                          AND COALESCE(u."SmimeCertificatePem", '') <> ''
                          AND NOT EXISTS (SELECT 1 FROM "EnrolledCertificates" e WHERE e."UserId" = u."Id")
                          AND NOT EXISTS (SELECT 1 FROM "Notifications" n WHERE n."RecipientUserId" = u."Id" AND n."Type" = 22);
                        IF uncarried > 0 THEN
                            RAISE EXCEPTION
                                'Cannot drop Users.SmimeCertificatePem: % active user(s) hold a self-service certificate that was '
                                'never carried into the certificate registry. Run v0.56.0 first, choose a folder for reader '
                                'certificates on every tenant (Tenant tab, Reader certificates), start it once so it carries them, '
                                'then upgrade again.', uncarried;
                        END IF;
                    END $$;
                    """);
            }

            migrationBuilder.DropColumn(
                name: "ImapPasswordHash",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SmimeCertificatePem",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "WebDavPasswordHash",
                table: "Users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The columns come back EMPTY: v0.56.0 no longer reads them, so a rollback to it works without their values.
            migrationBuilder.AddColumn<string>(
                name: "ImapPasswordHash",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmimeCertificatePem",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebDavPasswordHash",
                table: "Users",
                type: "text",
                nullable: true);
        }
    }
}
