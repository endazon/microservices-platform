using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataSourceService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDataSourceOwnerMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FR-05, UC-04, SC-06, ADR-0074 決定 1 (#1194): `owner` の写像表
            // （ソース側の利用者識別子 → 基盤の利用者識別子）を jsonb で持つ。
            //
            // 🔴 **既定値は空文字ではなく `{}` である。** `dotnet ef` は非 null の string 列へ
            // `defaultValue: ""` を書くが、**空文字は jsonb として不正であり**、既存行の backfill が
            // 失敗するか、通っても読み出し時の `JsonSerializer.Deserialize<Dictionary<...>>` が落ちる。
            // 前例（`AddDataSourceDefaultAttributes`）も JSON リテラルを既定値に置いている。
            //
            // **`DefaultAttributes` と違い、既存行へ意味のある値を backfill しない** ——
            // 写像表には「解決できなかったときの予約値」に当たるものが無く、
            // 空の表は「写像が 1 件も無い」という正しい状態である（ADR-0074 決定 3。
            // **器を入れても現在登録されているデータソースの予約値は減らない**）。
            migrationBuilder.AddColumn<string>(
                name: "OwnerMappings",
                table: "DataSources",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnerMappings",
                table: "DataSources");
        }
    }
}
