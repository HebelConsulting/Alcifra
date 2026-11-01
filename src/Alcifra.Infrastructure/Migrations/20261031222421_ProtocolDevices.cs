using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alcifra.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProtocolDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProtocolDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CredentialKey = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    SecretHash = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    MailEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    FilesEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CalendarsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ContactsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    EncryptMail = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProtocolDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProtocolDevices_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProtocolDevices_CredentialKey",
                table: "ProtocolDevices",
                column: "CredentialKey",
                unique: true,
                filter: "\"CredentialKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProtocolDevices_TenantId",
                table: "ProtocolDevices",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ProtocolDevices_UserId_Name",
                table: "ProtocolDevices",
                columns: new[] { "UserId", "Name" },
                unique: true);

            // Today's two passwords become legacy devices (#1970, owner 2026-10-29), so no device breaks on upgrade:
            // the same hash, the protocols each reached before. Every user with a hash, active or not, so a reactivated
            // account keeps them. The Users columns stay until a later, allowlisted migration drops them.
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                migrationBuilder.Sql("""
                    INSERT INTO "ProtocolDevices" ("Id", "TenantId", "UserId", "Name", "CredentialKey", "SecretHash", "Kind",
                        "MailEnabled", "FilesEnabled", "CalendarsEnabled", "ContactsEnabled", "EncryptMail", "CreatedAt",
                        "LastUsedAt", "ConcurrencyToken")
                    SELECT gen_random_uuid(), u."TenantId", u."Id", 'Earlier WebDAV/calendar password', NULL,
                        u."WebDavPasswordHash", 1, FALSE, TRUE, TRUE, TRUE, FALSE, now(), NULL, gen_random_uuid()
                    FROM "Users" u
                    WHERE u."WebDavPasswordHash" IS NOT NULL;

                    INSERT INTO "ProtocolDevices" ("Id", "TenantId", "UserId", "Name", "CredentialKey", "SecretHash", "Kind",
                        "MailEnabled", "FilesEnabled", "CalendarsEnabled", "ContactsEnabled", "EncryptMail", "CreatedAt",
                        "LastUsedAt", "ConcurrencyToken")
                    SELECT gen_random_uuid(), u."TenantId", u."Id", 'Earlier IMAP password', NULL,
                        u."ImapPasswordHash", 2, TRUE, FALSE, FALSE, FALSE, FALSE, now(), NULL, gen_random_uuid()
                    FROM "Users" u
                    WHERE u."ImapPasswordHash" IS NOT NULL;
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProtocolDevices");
        }
    }
}
