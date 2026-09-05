using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GraphService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGraphDocumentTermProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FR-18, ADR-0051 決定 1, IADR-0380 (#1244): 文書ごとの語の出現数（類似度候補の材料）。
            // 本文は持たない（上位 128 語と出現数の jsonb）。graph_documents への FK は張らない（edges と同じ理由）。
            // 既存文書の backfill は行わない —— 行が無い文書は供給元が表題から作る。
            migrationBuilder.CreateTable(
                name: "graph_document_term_profiles",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Terms = table.Column<string>(type: "jsonb", nullable: false),
                    BodyHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_graph_document_term_profiles", x => x.DocumentId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "graph_document_term_profiles");
        }
    }
}
