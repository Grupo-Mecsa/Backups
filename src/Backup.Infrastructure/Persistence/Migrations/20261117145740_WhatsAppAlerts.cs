using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backup.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WhatsAppAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UsePlatformWhatsApp",
                table: "NotificationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WahaApiKey",
                table: "NotificationSettings",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WahaSession",
                table: "NotificationSettings",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WahaUrl",
                table: "NotificationSettings",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WhatsAppEnabled",
                table: "NotificationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "WhatsAppOnFailure",
                table: "NotificationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "WhatsAppOnSuccess",
                table: "NotificationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WhatsAppRecipients",
                table: "NotificationSettings",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UsePlatformWhatsApp",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "WahaApiKey",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "WahaSession",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "WahaUrl",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "WhatsAppEnabled",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "WhatsAppOnFailure",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "WhatsAppOnSuccess",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "WhatsAppRecipients",
                table: "NotificationSettings");
        }
    }
}
