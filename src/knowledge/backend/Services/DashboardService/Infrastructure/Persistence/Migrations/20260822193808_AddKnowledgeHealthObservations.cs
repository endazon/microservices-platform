using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DashboardService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeHealthObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KnowledgeHealthObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Indicator = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubjectKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DocScope = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeHealthObservations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeHealthObservations_Indicator_DocScope",
                table: "KnowledgeHealthObservations",
                columns: new[] { "Indicator", "DocScope" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KnowledgeHealthObservations");
        }
    }
}
