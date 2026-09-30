using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backup.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Restores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Restores",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ArtifactName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    TargetProvider = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    TargetSummary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    RequestedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    Log = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Restores", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Restores_TenantId_StartedAt",
                table: "Restores",
                columns: new[] { "TenantId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Restores");
        }
    }
}
