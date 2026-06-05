using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stampd.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// v1.2 #93 — server-side sort columns for the worker / list surfaces. PostgreSQL
    /// variant of the SQLite V12EpochSortColumns migration.
    ///
    /// PostgreSQL's <c>timestamp with time zone</c> sorts cleanly without the SQLite
    /// text-sort gotcha, so an adopter on Postgres doesn't strictly need this column for
    /// correctness — but the entity model declares it and the runtime relies on it, so
    /// the migration is mandatory for any deployment that consumes
    /// <c>Stampd.Infrastructure.Postgres</c> at v1.3 or later. Backfill uses
    /// <c>EXTRACT(EPOCH FROM ...) * 1000</c>.
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

            // Backfill from the existing timestamp-with-time-zone columns. EXTRACT(EPOCH FROM ...)
            // returns the seconds-since-Unix-epoch as a double; multiply by 1000 and cast
            // to bigint for millisecond precision. Postgres identifiers are case-sensitive
            // when quoted — EF Core's npgsql provider stores Stampd tables/columns in
            // PascalCase, so the quoting must match exactly.
            migrationBuilder.Sql(@"
                UPDATE ""DocumentTemplates""
                SET ""CreatedAtUtcEpochMs"" = (EXTRACT(EPOCH FROM ""CreatedAtUtc"") * 1000)::bigint
                WHERE ""CreatedAtUtc"" IS NOT NULL;
            ");

            migrationBuilder.Sql(@"
                UPDATE ""BulkSendJobs""
                SET ""CreatedAtUtcEpochMs"" = (EXTRACT(EPOCH FROM ""CreatedAtUtc"") * 1000)::bigint
                WHERE ""CreatedAtUtc"" IS NOT NULL;
            ");

            migrationBuilder.Sql(@"
                UPDATE ""WebhookDeliveries""
                SET ""NextAttemptAtUtcEpochMs"" = (EXTRACT(EPOCH FROM ""NextAttemptAtUtc"") * 1000)::bigint
                WHERE ""NextAttemptAtUtc"" IS NOT NULL;
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
