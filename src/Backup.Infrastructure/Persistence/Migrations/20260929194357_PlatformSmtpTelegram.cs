using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backup.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlatformSmtpTelegram : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UsePlatformSmtp",
                table: "NotificationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "TelegramSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChatTitle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsGroup = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyOnFailure = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyOnSuccess = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramSubscriptions_ChatId",
                table: "TelegramSubscriptions",
                column: "ChatId");

            migrationBuilder.CreateIndex(
                name: "IX_TelegramSubscriptions_TenantId_ChatId",
                table: "TelegramSubscriptions",
                columns: new[] { "TenantId", "ChatId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelegramSubscriptions");

            migrationBuilder.DropColumn(
                name: "UsePlatformSmtp",
                table: "NotificationSettings");
        }
    }
}
