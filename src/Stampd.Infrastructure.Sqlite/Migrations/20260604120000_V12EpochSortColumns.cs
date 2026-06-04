using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stampd.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// v1.2 #93 — server-side sort columns for the worker / list surfaces.
    ///
    /// SQLite stores DateTimeOffset as TEXT, and its text-sort can't disambiguate timestamps
    /// across offsets. Up to v1.1 the templates list, the bulk-send worker, and the webhook
    /// delivery worker all worked around this by materializing candidate rows and sorting
    /// client-side. v1.2 adds a long Unix-epoch-milliseconds shadow column to each of those
    /// three tables, indexes them, and backfills the existing rows by walking the SQLite
    /// julianday() of the original TEXT timestamps.
    /// </summary>
    public partial class V12EpochSortColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- DocumentTemplates.CreatedAtUtcEpochMs ----
            migrationBuilder.AddColumn<long>(
                name: "CreatedAtUtcEpochMs",
                table: "DocumentTemplates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // ---- BulkSendJobs.CreatedAtUtcEpochMs ----
            migrationBuilder.AddColumn<long>(
                name: "CreatedAtUtcEpochMs",
                table: "BulkSendJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // ---- WebhookDeliveries.NextAttemptAtUtcEpochMs ----
            migrationBuilder.AddColumn<long>(
                name: "NextAttemptAtUtcEpochMs",
                table: "WebhookDeliveries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // Backfill from the existing TEXT timestamps. SQLite's julianday() parses the
            // ISO-like text format EF Core writes for DateTimeOffset; we then convert
            // (julianday − 2440587.5) days × 86_400_000 ms/day into Unix epoch ms.
            // The CAST trims to integer milliseconds.
            migrationBuilder.Sql(@"
                UPDATE DocumentTemplates
                SET CreatedAtUtcEpochMs = CAST((julianday(CreatedAtUtc) - 2440587.5) * 86400000.0 AS INTEGER)
                WHERE CreatedAtUtc IS NOT NULL;
            ");

            migrationBuilder.Sql(@"
                UPDATE BulkSendJobs
                SET CreatedAtUtcEpochMs = CAST((julianday(CreatedAtUtc) - 2440587.5) * 86400000.0 AS INTEGER)
                WHERE CreatedAtUtc IS NOT NULL;
            ");

            migrationBuilder.Sql(@"
                UPDATE WebhookDeliveries
                SET NextAttemptAtUtcEpochMs = CAST((julianday(NextAttemptAtUtc) - 2440587.5) * 86400000.0 AS INTEGER)
                WHERE NextAttemptAtUtc IS NOT NULL;
            ");

            // ---- Sort indexes ----
            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplates_TenantId_CreatedAtUtcEpochMs",
                table: "DocumentTemplates",
                columns: new[] { "TenantId", "CreatedAtUtcEpochMs" });

            migrationBuilder.CreateIndex(
                name: "IX_BulkSendJobs_Status_CreatedAtUtcEpochMs",
                table: "BulkSendJobs",
                columns: new[] { "Status", "CreatedAtUtcEpochMs" });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_NextAttemptAtUtcEpochMs",
                table: "WebhookDeliveries",
                column: "NextAttemptAtUtcEpochMs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WebhookDeliveries_NextAttemptAtUtcEpochMs",
                table: "WebhookDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_BulkSendJobs_Status_CreatedAtUtcEpochMs",
                table: "BulkSendJobs");

            migrationBuilder.DropIndex(
                name: "IX_DocumentTemplates_TenantId_CreatedAtUtcEpochMs",
                table: "DocumentTemplates");

            migrationBuilder.DropColumn(
                name: "NextAttemptAtUtcEpochMs",
                table: "WebhookDeliveries");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtcEpochMs",
                table: "BulkSendJobs");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtcEpochMs",
                table: "DocumentTemplates");
        }
    }
}
