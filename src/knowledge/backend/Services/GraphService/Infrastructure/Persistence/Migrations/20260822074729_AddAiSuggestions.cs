using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GraphService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAiSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_suggestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    SourceDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    EdgeTypeId = table.Column<Guid>(type: "uuid", nullable: true),
                    TagValue = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Rationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false, defaultValue: ""),
                    State = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    RejectedCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    RejectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SourceFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TargetFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ReinstatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReinstatedReason = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_suggestions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_suggestions_endpoints",
                table: "ai_suggestions",
                columns: new[] { "SourceDocumentId", "TargetDocumentId" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_suggestions_state",
                table: "ai_suggestions",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_suggestions");
        }
    }
}
