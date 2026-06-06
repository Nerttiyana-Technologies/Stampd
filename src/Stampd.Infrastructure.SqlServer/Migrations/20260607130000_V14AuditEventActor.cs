using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stampd.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// v2.0 Slice D — SqlServer variant of V14: ActorUserId + ActorRole columns on
    /// AuditEvents for admin-operation attribution. Mirror of the SQLite migration.
    /// </summary>
    public partial class V14AuditEventActor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActorUserId",
                table: "AuditEvents",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActorRole",
                table: "AuditEvents",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_TenantId_ActorUserId_OccurredAtUtc",
                table: "AuditEvents",
                columns: new[] { "TenantId", "ActorUserId", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditEvents_TenantId_ActorUserId_OccurredAtUtc",
                table: "AuditEvents");

            migrationBuilder.DropColumn(name: "ActorRole", table: "AuditEvents");
            migrationBuilder.DropColumn(name: "ActorUserId", table: "AuditEvents");
        }
    }
}
