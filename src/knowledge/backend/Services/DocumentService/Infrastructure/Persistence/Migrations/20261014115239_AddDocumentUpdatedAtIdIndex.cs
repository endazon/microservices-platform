using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentService.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// FR-06, NFR-16, IADR-0537 (#1923・#1897): 文書の台帳へ一覧のページの複合索引 <c>(UpdatedAt, Id)</c> を張る。
    ///
    /// gRPC <c>ListDocuments</c> が、更新日時の降順・同時刻は ID の降順のキーセット
    /// （<c>UpdatedAt &lt;= c AND (UpdatedAt &lt; c OR Id &lt; id) ORDER BY UpdatedAt DESC, Id DESC LIMIT n</c>）をこれで引く。
    /// 降順は索引を後ろから辿る。
    ///
    /// **索引の追加だけで、行の書き換えは無い。** 起動時の <c>MigrateAsync</c> が当てる（<c>Program.cs</c>）。
    /// <c>CREATE INDEX</c> は作り終えるまで表への書き込みを待たせる。約 4 万件の規模では一瞬で終わる（IADR-0509 の同種の索引と同じ）。
    /// </summary>
    public partial class AddDocumentUpdatedAtIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Documents_UpdatedAt_Id",
                table: "Documents",
                columns: new[] { "UpdatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Documents_UpdatedAt_Id",
                table: "Documents");
        }
    }
}
