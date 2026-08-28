using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentAssetUris : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FR-12, ADR-0057 決定 1, IADR-0296: 図表資産の参照 URI（jsonb の文字列配列）。
            //
            // 🔴 **既定値は "[]" である。** EF が生成した既定は空文字列 "" だが、jsonb 列へ ''
            // を入れると Postgres が "invalid input syntax for type json" で拒否し、
            // **既存行を持つ環境で適用そのものが落ちる**（既存の Tags / Attributes も
            // '[]'::jsonb を空の表現に使っている。20260809123339_MigrateTagsToIdentifiers 参照）。
            //
            // 🔴 **既存文書へ資産 URI を遡及付与しない**（IADR-0296 決定 4）。全行が空配列で入る。
            // doc_scope を遡及付与しない方針（計画 ADR-0054 §結果）と同型の受容であり、
            // **本マイグレーション以前に取り込まれた文書の資産は台帳から辿れないままである。**
            migrationBuilder.AddColumn<string>(
                name: "AssetUris",
                table: "Documents",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssetUris",
                table: "Documents");
        }
    }
}
