using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stampd.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// v1.2 #93 — server-side sort columns for the worker / list surfaces. Mirror of the
    /// SQLite V12EpochSortColumns migration with SQL Server-specific column types and
    /// backfill SQL.
    ///
    /// SQL Server's <c>datetimeoffset</c> sorts cleanly without the SQLite text-sort
    /// gotcha, so an adopter on SQL Server doesn't strictly need this column for
    /// correctness — but the entity model declares it and the runtime relies on it, so
    /// the migration is mandatory for any deployment that consumes
    /// <c>Stampd.Infrastructure.SqlServer</c> at v1.3 or later. Backfill uses
    /// <c>DATEDIFF_BIG(MILLISECOND, ...)</c> which requires SQL Server 2016+.
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
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // ---- BulkSendJobs.CreatedAtUtcEpochMs ----
            migrationBuilder.AddColumn<long>(
                name: "CreatedAtUtcEpochMs",
                table: "BulkSendJobs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // ---- WebhookDeliveries.NextAttemptAtUtcEpochMs ----
            migrationBuilder.AddColumn<long>(
                name: "NextAttemptAtUtcEpochMs",
                table: "WebhookDeliveries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Backfill from the existing datetimeoffset columns. DATEDIFF_BIG returns the
            // signed count of MILLISECOND boundaries crossed between the two arguments —
            // for our forward-only use case that's exactly Unix epoch milliseconds.
            migrationBuilder.Sql(@"
                UPDATE DocumentTemplates
                SET CreatedAtUtcEpochMs = DATEDIFF_BIG(MILLISECOND, '1970-01-01T00:00:00+00:00', CreatedAtUtc)
                WHERE CreatedAtUtc IS NOT NULL;
            ");

            migrationBuilder.Sql(@"
                UPDATE BulkSendJobs
                SET CreatedAtUtcEpochMs = DATEDIFF_BIG(MILLISECOND, '1970-01-01T00:00:00+00:00', CreatedAtUtc)
                WHERE CreatedAtUtc IS NOT NULL;
            ");

            migrationBuilder.Sql(@"
                UPDATE WebhookDeliveries
                SET NextAttemptAtUtcEpochMs = DATEDIFF_BIG(MILLISECOND, '1970-01-01T00:00:00+00:00', NextAttemptAtUtc)
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
