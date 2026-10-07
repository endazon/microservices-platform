using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentService.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// FR-02, FR-06, NFR-08, IADR-0509 (#1765): 文書の台帳へキーセットの複合索引 <c>(CreatedAt, Id)</c> を張る。
    ///
    /// <c>GET /documents/page</c> と再発行の口（<c>POST /documents/republish-updated</c>）が、作成時刻昇順・同時刻は ID 昇順の
    /// キーセット（<c>CreatedAt &gt;= c AND (CreatedAt &gt; c OR Id &gt; id) ORDER BY CreatedAt, Id LIMIT n</c>）をこれで引く。
    ///
    /// **索引の追加だけで、行の書き換えは無い。** 起動時の <c>MigrateAsync</c> が当てる（<c>Program.cs</c>）。
    /// <c>CREATE INDEX</c> は作り終えるまで表への書き込みを待たせる（<c>CONCURRENTLY</c> は移行の取引の中で使えない）。
    /// 経路B の規模（約 2 万件）では一瞬で終わる。
    /// </summary>
    public partial class AddDocumentCreatedAtIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Documents_CreatedAt_Id",
                table: "Documents",
                columns: new[] { "CreatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Documents_CreatedAt_Id",
                table: "Documents");
        }
    }
}
