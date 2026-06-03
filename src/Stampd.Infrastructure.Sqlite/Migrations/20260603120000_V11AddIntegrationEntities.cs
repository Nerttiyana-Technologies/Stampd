using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stampd.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class V11AddIntegrationEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // v1.1 #88 — per-recipient submitted values for multi-recipient aggregation.
            migrationBuilder.AddColumn<string>(
                name: "SubmittedFieldValuesJson",
                table: "Recipients",
                type: "TEXT",
                nullable: true);

            // v1.1 #83 — persistent OTP challenges (hashed code at rest).
            migrationBuilder.CreateTable(
                name: "OtpChallenges",
                columns: table => new
                {
                    VerificationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Identifier = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    CodeHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Salt = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FailedAttempts = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OtpChallenges", x => x.VerificationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OtpChallenges_ExpiresAtUtc",
                table: "OtpChallenges",
                column: "ExpiresAtUtc");

            // v1.1 #87 — bulk-send jobs (worker drains PendingRowsJson).
            migrationBuilder.CreateTable(
                name: "BulkSendJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DocumentTemplateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalRows = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedRows = table.Column<int>(type: "INTEGER", nullable: false),
                    FailedRows = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    PendingRowsJson = table.Column<string>(type: "TEXT", nullable: false),
                    FailedRowsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BulkSendJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BulkSendJobs_TenantId_Status",
                table: "BulkSendJobs",
                columns: new[] { "TenantId", "Status" });

            // v1.1 #86 — webhook subscribers.
            migrationBuilder.CreateTable(
                name: "WebhookEndpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Secret = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SubscribedEvents = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastDeliveryAttemptAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastSuccessAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookEndpoints", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEndpoints_TenantId_IsActive",
                table: "WebhookEndpoints",
                columns: new[] { "TenantId", "IsActive" });

            // v1.1 #86 — webhook delivery outbox (worker drains by NextAttemptAtUtc).
            migrationBuilder.CreateTable(
                name: "WebhookDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WebhookEndpointId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventType = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    LastResponseStatus = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebhookDeliveries_WebhookEndpoints_WebhookEndpointId",
                        column: x => x.WebhookEndpointId,
                        principalTable: "WebhookEndpoints",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_NextAttemptAtUtc",
                table: "WebhookDeliveries",
                column: "NextAttemptAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookDeliveries_WebhookEndpointId",
                table: "WebhookDeliveries",
                column: "WebhookEndpointId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "WebhookDeliveries");
            migrationBuilder.DropTable(name: "WebhookEndpoints");
            migrationBuilder.DropTable(name: "BulkSendJobs");
            migrationBuilder.DropTable(name: "OtpChallenges");

            migrationBuilder.DropColumn(
                name: "SubmittedFieldValuesJson",
                table: "Recipients");
        }
    }
}
