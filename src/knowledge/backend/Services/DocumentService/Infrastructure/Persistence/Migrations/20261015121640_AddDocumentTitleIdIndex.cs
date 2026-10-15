using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentService.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// FR-06, SC-05, IADR-0545 (#1923): 文書の台帳へ一覧の表題の並べ替えの複合索引 <c>(Title, Id)</c> を張る。
    ///
    /// gRPC <c>ListDocuments</c> の表題の昇順・降順のキーセット
    /// （<c>Title &gt;= t AND (Title &gt; t OR Id &gt; id) ORDER BY Title, Id LIMIT n</c>。降順は索引を後ろから辿る）をこれで引く。
    /// 表題は最大 500 文字で、btree の 1 項目の上限に収まる。
    ///
    /// **索引の追加だけで、行の書き換えは無い。** 起動時の <c>MigrateAsync</c> が当てる（<c>Program.cs</c>）。
    /// <c>CREATE INDEX</c> は作り終えるまで表への書き込みを待たせる。約 4 万件の規模では一瞬で終わる（IADR-0537 の同種の索引と同じ）。
    /// </summary>
    public partial class AddDocumentTitleIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Documents_Title_Id",
                table: "Documents",
                columns: new[] { "Title", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Documents_Title_Id",
                table: "Documents");
        }
    }
}
