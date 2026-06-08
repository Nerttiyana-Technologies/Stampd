using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stampd.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// v2.1 #212 — strict completed-date sort for the signing-requests admin list.
    ///
    /// Adds a nullable <c>CompletedAtUtcEpochMs</c> shadow column to SigningRequests
    /// so the v2 Slice B "completed" sort can ORDER BY a portable bigint instead of
    /// the DateTimeOffset? column that EF Core 10's SQLite provider can't translate
    /// for ORDER BY (the Doc 16 / v1.2 #115 family of bugs). Pattern mirrors v1.3
    /// #133 — backfill via SQLite's julianday(), then add a covering composite
    /// index on (TenantId, CompletedAtUtcEpochMs). Nullable: in-flight workflows
    /// have a null shadow until completion.
    /// </summary>
    public partial class V15CompletedAtUtcEpochMs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CompletedAtUtcEpochMs",
                table: "SigningRequests",
                type: "INTEGER",
                nullable: true);

            // Backfill from existing CompletedAtUtc values using SQLite's julianday()
            // (same formula used by V12 / V13). Rows still in flight stay null.
            migrationBuilder.Sql(@"
                UPDATE SigningRequests
                SET CompletedAtUtcEpochMs = CAST((julianday(CompletedAtUtc) - 2440587.5) * 86400000.0 AS INTEGER)
                WHERE CompletedAtUtc IS NOT NULL;
            ");

            migrationBuilder.CreateIndex(
                name: "IX_SigningRequests_TenantId_CompletedAtUtcEpochMs",
                table: "SigningRequests",
                columns: new[] { "TenantId", "CompletedAtUtcEpochMs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SigningRequests_TenantId_CompletedAtUtcEpochMs",
                table: "SigningRequests");

            migrationBuilder.DropColumn(
                name: "CompletedAtUtcEpochMs",
                table: "SigningRequests");
        }
    }
}
