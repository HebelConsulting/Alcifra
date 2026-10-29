using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alcifra.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PushTransportApns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "P256dh",
                table: "PhonePushRegistrations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "Auth",
                table: "PhonePushRegistrations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AddColumn<string>(
                name: "ApnsEnvironment",
                table: "PhonePushRegistrations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Transport",
                table: "PhonePushRegistrations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "webpush");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PhonePushRegistrations_Transport",
                table: "PhonePushRegistrations",
                sql: "(CASE WHEN \"Transport\" = 'webpush' AND \"P256dh\" IS NOT NULL AND \"Auth\" IS NOT NULL AND \"ApnsEnvironment\" IS NULL THEN 1 WHEN \"Transport\" = 'apns' AND \"P256dh\" IS NULL AND \"Auth\" IS NULL AND \"ApnsEnvironment\" IN ('sandbox', 'production') THEN 1 ELSE 0 END) = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PhonePushRegistrations_Transport",
                table: "PhonePushRegistrations");

            migrationBuilder.DropColumn(
                name: "ApnsEnvironment",
                table: "PhonePushRegistrations");

            migrationBuilder.DropColumn(
                name: "Transport",
                table: "PhonePushRegistrations");

            migrationBuilder.AlterColumn<string>(
                name: "P256dh",
                table: "PhonePushRegistrations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Auth",
                table: "PhonePushRegistrations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);
        }
    }
}
