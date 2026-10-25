using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GraphService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGraphDocumentSyncMarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "graph_document_sync_marks",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AppliedSequence = table.Column<long>(type: "bigint", nullable: true),
                    Deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_graph_document_sync_marks", x => x.DocumentId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "graph_document_sync_marks");
        }
    }
}
