using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimplArchive.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPhonePushRegistrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PhoneEnabled",
                table: "UserNotificationPreferences",
                type: "boolean",
                nullable: false,
                // TRUE, not the generated false: an existing row is an email choice the user made, and the phone
                // channel was never off for them (a missing row means on). Only the migration carries the default;
                // the model has none, because a database default of true makes EF drop an explicit false on insert.
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "PhonePushRegistrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    App = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeviceLabel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    P256dh = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Auth = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastRegisteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhonePushRegistrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhonePushRegistrations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PhonePushRegistrations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PhonePushRegistrations_TenantId_Endpoint",
                table: "PhonePushRegistrations",
                columns: new[] { "TenantId", "Endpoint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhonePushRegistrations_TenantId_UserId",
                table: "PhonePushRegistrations",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_PhonePushRegistrations_UserId",
                table: "PhonePushRegistrations",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PhonePushRegistrations");

            migrationBuilder.DropColumn(
                name: "PhoneEnabled",
                table: "UserNotificationPreferences");
        }
    }
}
