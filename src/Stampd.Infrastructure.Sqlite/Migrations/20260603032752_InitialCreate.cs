using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stampd.Infrastructure.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    SourcePdfStorageKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SourcePdfSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SigningRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DocumentTemplateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SentAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeclinedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    VoidedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    TerminationReason = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ConcurrencyToken = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigningRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SigningRequests_DocumentTemplates_DocumentTemplateId",
                        column: x => x.DocumentTemplateId,
                        principalTable: "DocumentTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TemplateRecipientRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DocumentTemplateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RoutingOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    RequiresIdentityVerification = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemplateRecipientRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TemplateRecipientRoles_DocumentTemplates_DocumentTemplateId",
                        column: x => x.DocumentTemplateId,
                        principalTable: "DocumentTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SignedDocumentRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SigningRequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StorageKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SignedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SealingProviderName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SealingKeyIdentifier = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    TimestampAuthorityUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    PAdESLevel = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignedDocumentRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SignedDocumentRecords_SigningRequests_SigningRequestId",
                        column: x => x.SigningRequestId,
                        principalTable: "SigningRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Recipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SigningRequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RoutingOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    InvitedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FirstViewedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SignedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DeclinedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    IdentityVerifiedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    IdentityVerificationMethod = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DeclineReason = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    AccessToken = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Recipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Recipients_SigningRequests_SigningRequestId",
                        column: x => x.SigningRequestId,
                        principalTable: "SigningRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Recipients_TemplateRecipientRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "TemplateRecipientRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TemplateFields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DocumentTemplateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssignedRoleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PageNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    BoundsX = table.Column<double>(type: "REAL", nullable: false),
                    BoundsY = table.Column<double>(type: "REAL", nullable: false),
                    BoundsWidth = table.Column<double>(type: "REAL", nullable: false),
                    BoundsHeight = table.Column<double>(type: "REAL", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    IsRequired = table.Column<bool>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DefaultValue = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemplateFields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TemplateFields_DocumentTemplates_DocumentTemplateId",
                        column: x => x.DocumentTemplateId,
                        principalTable: "DocumentTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TemplateFields_TemplateRecipientRoles_AssignedRoleId",
                        column: x => x.AssignedRoleId,
                        principalTable: "TemplateRecipientRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SigningRequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipientId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EventType = table.Column<int>(type: "INTEGER", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    GeoCountry = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    GeoCity = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DocumentHashAtEvent = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    IsRedacted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditEvents_Recipients_RecipientId",
                        column: x => x.RecipientId,
                        principalTable: "Recipients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AuditEvents_SigningRequests_SigningRequestId",
                        column: x => x.SigningRequestId,
                        principalTable: "SigningRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_RecipientId",
                table: "AuditEvents",
                column: "RecipientId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_SigningRequestId_OccurredAtUtc",
                table: "AuditEvents",
                columns: new[] { "SigningRequestId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_TenantId_EventType",
                table: "AuditEvents",
                columns: new[] { "TenantId", "EventType" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_TenantId_OccurredAtUtc",
                table: "AuditEvents",
                columns: new[] { "TenantId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplates_TenantId_IsArchived",
                table: "DocumentTemplates",
                columns: new[] { "TenantId", "IsArchived" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTemplates_TenantId_Name",
                table: "DocumentTemplates",
                columns: new[] { "TenantId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Recipients_AccessToken",
                table: "Recipients",
                column: "AccessToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Recipients_RoleId",
                table: "Recipients",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Recipients_SigningRequestId_RoutingOrder",
                table: "Recipients",
                columns: new[] { "SigningRequestId", "RoutingOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_SignedDocumentRecords_ContentSha256",
                table: "SignedDocumentRecords",
                column: "ContentSha256");

            migrationBuilder.CreateIndex(
                name: "IX_SignedDocumentRecords_SigningRequestId",
                table: "SignedDocumentRecords",
                column: "SigningRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SignedDocumentRecords_TenantId_SignedAtUtc",
                table: "SignedDocumentRecords",
                columns: new[] { "TenantId", "SignedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SigningRequests_DocumentTemplateId",
                table: "SigningRequests",
                column: "DocumentTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_SigningRequests_TenantId_CreatedAtUtc",
                table: "SigningRequests",
                columns: new[] { "TenantId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SigningRequests_TenantId_Status",
                table: "SigningRequests",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TemplateFields_AssignedRoleId",
                table: "TemplateFields",
                column: "AssignedRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_TemplateFields_DocumentTemplateId_PageNumber",
                table: "TemplateFields",
                columns: new[] { "DocumentTemplateId", "PageNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_TemplateRecipientRoles_DocumentTemplateId_RoutingOrder",
                table: "TemplateRecipientRoles",
                columns: new[] { "DocumentTemplateId", "RoutingOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "SignedDocumentRecords");

            migrationBuilder.DropTable(
                name: "TemplateFields");

            migrationBuilder.DropTable(
                name: "Recipients");

            migrationBuilder.DropTable(
                name: "SigningRequests");

            migrationBuilder.DropTable(
                name: "TemplateRecipientRoles");

            migrationBuilder.DropTable(
                name: "DocumentTemplates");
        }
    }
}
