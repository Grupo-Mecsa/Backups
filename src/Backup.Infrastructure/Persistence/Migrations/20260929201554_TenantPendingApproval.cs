using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backup.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantPendingApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PendingApproval",
                table: "Tenants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PendingApproval",
                table: "Tenants");
        }
    }
}
