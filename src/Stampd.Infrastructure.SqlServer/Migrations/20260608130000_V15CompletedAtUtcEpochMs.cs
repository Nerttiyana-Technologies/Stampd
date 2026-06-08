using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stampd.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// v2.1 #212 — strict completed-date sort, SQL Server variant.
    /// Mirrors the SQLite V15 migration: adds nullable <c>CompletedAtUtcEpochMs</c>
    /// to SigningRequests, backfills via DATEDIFF_BIG, and adds the covering
    /// composite index on (TenantId, CompletedAtUtcEpochMs). Nullable: in-flight
    /// workflows have a null shadow until completion.
    /// </summary>
    public partial class V15CompletedAtUtcEpochMs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CompletedAtUtcEpochMs",
                table: "SigningRequests",
                type: "bigint",
                nullable: true);

            migrationBuilder.Sql(@"
                UPDATE SigningRequests
                SET CompletedAtUtcEpochMs = DATEDIFF_BIG(MILLISECOND, '1970-01-01T00:00:00+00:00', CompletedAtUtc)
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
