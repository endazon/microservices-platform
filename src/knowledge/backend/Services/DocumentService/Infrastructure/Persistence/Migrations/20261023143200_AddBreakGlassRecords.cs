using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBreakGlassRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BreakGlassAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Operation = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NomineeId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ViewTarget = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BreakGlassAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BreakGlassGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NomineeId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BreakGlassGrants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BreakGlassAuditEntries_OccurredAt",
                table: "BreakGlassAuditEntries",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_BreakGlassAuditEntries_OwnerId_OccurredAt",
                table: "BreakGlassAuditEntries",
                columns: new[] { "OwnerId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BreakGlassGrants_DocumentId_NomineeId",
                table: "BreakGlassGrants",
                columns: new[] { "DocumentId", "NomineeId" });

            migrationBuilder.CreateIndex(
                name: "IX_BreakGlassGrants_ExpiresAt",
                table: "BreakGlassGrants",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BreakGlassAuditEntries");

            migrationBuilder.DropTable(
                name: "BreakGlassGrants");
        }
    }
}
