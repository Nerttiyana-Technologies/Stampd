using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stampd.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    /// <summary>v3.0 alpha.1 — AdminScopes table, PostgreSQL variant.</summary>
    public partial class V16AdminScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminScopes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    GrantedByUserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false),
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
