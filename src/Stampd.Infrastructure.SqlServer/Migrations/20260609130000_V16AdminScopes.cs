using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stampd.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    /// <summary>v3.0 alpha.1 — AdminScopes table, SQL Server variant.</summary>
    public partial class V16AdminScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminScopes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GrantedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    GrantedByUserId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedByUserId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RevocationReason = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    ConcurrencyToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
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
