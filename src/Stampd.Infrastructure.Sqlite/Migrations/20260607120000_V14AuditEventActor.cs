using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stampd.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// v2.0 Slice D — actor attribution on AuditEvent. Adds nullable ActorUserId +
    /// ActorRole columns so admin operations (void, resend, demo cleanup) write a
    /// trace of "who did this." Pre-v2 audit rows keep NULL in both columns — that's
    /// the documented contract, no backfill needed.
    /// </summary>
    public partial class V14AuditEventActor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActorUserId",
                table: "AuditEvents",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActorRole",
                table: "AuditEvents",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            // Filtered-by-tenant index supporting the "show me all actions by user X"
            // and "show all admin actions" audit-console queries (the most common
            // tenant-admin investigation path). SQLite ignores the NULL exclusion in
            // SQL DDL, but the planner still uses the index for non-NULL probes.
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

            migrationBuilder.DropColumn(
                name: "ActorRole",
                table: "AuditEvents");

            migrationBuilder.DropColumn(
                name: "ActorUserId",
                table: "AuditEvents");
        }
    }
}
