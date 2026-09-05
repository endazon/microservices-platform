using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentService.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// FR-02, FR-03, FR-06, SC-03, ADR-0070 決定 3・決定 4, IADR-0381 (#1254 / #1253):
    /// 文書台帳へ「原本が本文を持っていたか」（HasBody）と「原本の所在・データソースの表示名」
    /// （OriginalPath / DataSourceName）を足す。
    ///
    /// **追加だけで、既存行の書き換えは無い。** HasBody の DEFAULT は true で、
    /// 既存文書は従来どおり「本文あり」として読める（欠落＝本文ありという
    /// IADR-0358 決定 3 の読みと同じ向き）。所在は既存行では null のままである。
    ///
    /// 🔴 **索引の backfill はここでは行わない。** 既に索引済みの本文なし文書の索引テキストは
    /// 題名・タグのままであり、所在で当たるようになるのは**次の同期で再取得された後**である
    /// （台帳が所在を知らないので、再索引しても足す材料が無い）。
    /// **再同期は冪等である** —— 文書 ID は (sourceId, path) から決定的に導出され
    /// （DeterministicGuid.ForDocument）、メタデータ点の ID も文書 ID から決定的に導出され
    /// （ChunkId.DeriveMetadata）、取り込みは全コレクションから削除してから upsert する。
    /// 何度流しても点は増えず、同じ 1 点が上書きされる。
    /// </summary>
    public partial class AddDocumentBodyPresenceAndOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DataSourceName",
                table: "Documents",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasBody",
                table: "Documents",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginalPath",
                table: "Documents",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DataSourceName",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "HasBody",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "OriginalPath",
                table: "Documents");
        }
    }
}
