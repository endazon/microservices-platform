using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GraphService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGraphDocumentTagsAndEdgeAutoSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AutoSource",
                table: "edges",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            // [[IADR-0521]] (#1396): 既存の自動抽出の辺は、すべて本文のリンク由来である（共有タグの辺は本移行より後にしか無い）。
            // 埋めないと、本文のリンクの差分（`AutoSource=link` を前提にする後着の作り直し）から外れる。
            migrationBuilder.Sql("UPDATE edges SET \"AutoSource\" = 'link' WHERE \"Provenance\" = 'auto';");

            migrationBuilder.AddColumn<string>(
                name: "Anchor",
                table: "document_link_targets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExplicitTypeName",
                table: "document_link_targets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "document_link_targets",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "graph_document_tags",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tag = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_graph_document_tags", x => new { x.DocumentId, x.Tag });
                });

            migrationBuilder.CreateIndex(
                name: "ix_graph_document_tags_tag",
                table: "graph_document_tags",
                column: "Tag");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "graph_document_tags");

            // [[IADR-0521]] (#1396): 共有タグの辺は内訳列が無いと本文のリンクの辺と見分けられない（どちらも出所 `auto`）。
            // 列だけ落とすと、本文のリンクの差分（起点の辺）にも共有タグの差分にも属さない辺が残り続ける。先に消す。
            migrationBuilder.Sql("DELETE FROM edges WHERE \"AutoSource\" = 'tag';");

            migrationBuilder.DropColumn(
                name: "AutoSource",
                table: "edges");

            migrationBuilder.DropColumn(
                name: "Anchor",
                table: "document_link_targets");

            migrationBuilder.DropColumn(
                name: "ExplicitTypeName",
                table: "document_link_targets");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "document_link_targets");
        }
    }
}
