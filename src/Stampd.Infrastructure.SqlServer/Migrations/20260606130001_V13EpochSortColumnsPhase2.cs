using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stampd.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// v1.3 #133 — second pass of the epoch sort columns pattern, SQL Server variant.
    /// Mirrors <c>20260606120000_V13EpochSortColumnsPhase2</c> in the SQLite provider:
    /// adds <c>CreatedAtUtcEpochMs</c> to SigningRequests and <c>SignedAtUtcEpochMs</c>
    /// to SignedDocumentRecords, backfills, and indexes.
    /// </summary>
    public partial class V13EpochSortColumnsPhase2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CreatedAtUtcEpochMs",
                table: "SigningRequests",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SignedAtUtcEpochMs",
                table: "SignedDocumentRecords",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.Sql(@"
                UPDATE SigningRequests
                SET CreatedAtUtcEpochMs = DATEDIFF_BIG(MILLISECOND, '1970-01-01T00:00:00+00:00', CreatedAtUtc)
                WHERE CreatedAtUtc IS NOT NULL;
            ");

            migrationBuilder.Sql(@"
                UPDATE SignedDocumentRecords
                SET SignedAtUtcEpochMs = DATEDIFF_BIG(MILLISECOND, '1970-01-01T00:00:00+00:00', SignedAtUtc)
                WHERE SignedAtUtc IS NOT NULL;
            ");

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
