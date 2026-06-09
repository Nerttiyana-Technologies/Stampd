using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stampd.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// v3.0 alpha.1 — per-tenant admin assignments. Adds the AdminScopes table with
    /// composite (UserId, TenantId) index for the hot "is this user admin on this
    /// tenant" check, plus a secondary TenantId index for the per-tenant scope list.
    /// </summary>
    public partial class V16AdminScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminScopes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GrantedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    GrantedByUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RevokedByUserId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    RevocationReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    ConcurrencyToken = table.Column<Guid>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminScopes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminScopes_TenantId",
                table: "AdminScopes",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminScopes_UserId_TenantId",
                table: "AdminScopes",
                columns: new[] { "UserId", "TenantId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AdminScopes");
        }
    }
}
