using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stampd.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// v1.3 #133 — second pass of the epoch sort columns pattern.
    ///
    /// v1.2's V12 migration added epoch shadow columns to DocumentTemplate, BulkSendJob,
    /// and WebhookDelivery so SQLite could ORDER BY / WHERE them server-side. v1.3 extends
    /// the same pattern to SigningRequest (powering <c>GET /api/signing-requests</c>) and
    /// SignedDocumentRecord (powering the recipient signed-document download endpoint),
    /// closing out the materialize-and-sort workarounds those surfaces still had.
    /// </summary>
    public partial class V13EpochSortColumnsPhase2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- SigningRequests.CreatedAtUtcEpochMs ----
            migrationBuilder.AddColumn<long>(
                name: "CreatedAtUtcEpochMs",
                table: "SigningRequests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // ---- SignedDocumentRecords.SignedAtUtcEpochMs ----
            migrationBuilder.AddColumn<long>(
                name: "SignedAtUtcEpochMs",
                table: "SignedDocumentRecords",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            // Backfill via SQLite's julianday() — same formula as V12. Days since the
            // Julian Day origin minus the Julian Day of the Unix epoch (2440587.5), times
            // 86_400_000 ms/day, cast to integer milliseconds.
            migrationBuilder.Sql(@"
                UPDATE SigningRequests
                SET CreatedAtUtcEpochMs = CAST((julianday(CreatedAtUtc) - 2440587.5) * 86400000.0 AS INTEGER)
                WHERE CreatedAtUtc IS NOT NULL;
            ");

            migrationBuilder.Sql(@"
                UPDATE SignedDocumentRecords
                SET SignedAtUtcEpochMs = CAST((julianday(SignedAtUtc) - 2440587.5) * 86400000.0 AS INTEGER)
                WHERE SignedAtUtc IS NOT NULL;
            ");

            // ---- Sort indexes ----
            migrationBuilder.CreateIndex(
                name: "IX_SigningRequests_TenantId_CreatedAtUtcEpochMs",
                table: "SigningRequests",
                columns: new[] { "TenantId", "CreatedAtUtcEpochMs" });

            migrationBuilder.CreateIndex(
                name: "IX_SignedDocumentRecords_SignedAtUtcEpochMs",
                table: "SignedDocumentRecords",
                column: "SignedAtUtcEpochMs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SignedDocumentRecords_SignedAtUtcEpochMs",
                table: "SignedDocumentRecords");

            migrationBuilder.DropIndex(
                name: "IX_SigningRequests_TenantId_CreatedAtUtcEpochMs",
                table: "SigningRequests");

            migrationBuilder.DropColumn(
                name: "SignedAtUtcEpochMs",
                table: "SignedDocumentRecords");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtcEpochMs",
                table: "SigningRequests");
        }
    }
}
