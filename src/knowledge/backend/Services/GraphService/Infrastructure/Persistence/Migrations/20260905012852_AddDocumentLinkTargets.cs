using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GraphService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentLinkTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FR-10, FR-17, SC-10, IADR-0389 (#1246): 本文が指すリンク先の**名前**。
            //
            // 🔴 **保存するのは解決の失敗ではなく名前である。** 失敗を保存すると、
            // 相手の文書が改名・削除されて他文書のリンクが壊れても、その文書が再取り込みされる
            // まで未解決リンク数に現れない。名前を保存し、収集のたびに解決し直す（決定 3）。
            //
            // **backfill はしない。** 既存文書のリンク先は本文からしか復元できず、
            // 本移行では本文を持たない（正本は DocumentService。ADR-0002）。
            // 各文書の次の `DocumentUpdated` で埋まる。**それまで未解決リンク数は過少である** ——
            // 0 を「リンク切れ無し」と読ませないため、[[IADR-0389]] §測っていないことに明記した。
            migrationBuilder.CreateTable(
                name: "document_link_targets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Target = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ExtractedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_link_targets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_link_targets_source",
                table: "document_link_targets",
                column: "SourceDocumentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_link_targets");
        }
    }
}
