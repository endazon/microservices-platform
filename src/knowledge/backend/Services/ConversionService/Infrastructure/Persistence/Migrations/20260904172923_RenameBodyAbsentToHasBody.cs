using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConversionService.Migrations
{
    /// <summary>
    /// FR-12, SC-07, ADR-0070 決定 3, IADR-0388 (#1254): `BodyAbsent`（否定形・既定 false）を
    /// `HasBody`（肯定形・既定 true）へ改名し、**極性を反転する**。
    ///
    /// 🔴 **改名だけでは済まない。** 値の意味が反転するので、EF が既定で吐く
    /// 「古い列を落として新しい列を足す」形をそのまま採ると**既存行の内訳が全部消える**
    /// （落とした列の値を写す段が無い）。列を足す → **反転して写す** → 落とす、の順にする。
    ///
    /// 既定値も反転させる（`false`＝本文なしではない → `true`＝本文あり）。
    /// **どちらの綴りでも既定の意味は「本文あり」**であり、既存行の読みは変わらない。
    /// </summary>
    public partial class RenameBodyAbsentToHasBody : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasBody",
                table: "ConversionJobs",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // 既存行の内訳を写す。`BodyAbsent = true`（本文なし）だった行だけが `HasBody = false` になる。
            migrationBuilder.Sql(
                @"UPDATE ""ConversionJobs"" SET ""HasBody"" = NOT ""BodyAbsent"";");

            migrationBuilder.DropColumn(
                name: "BodyAbsent",
                table: "ConversionJobs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BodyAbsent",
                table: "ConversionJobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                @"UPDATE ""ConversionJobs"" SET ""BodyAbsent"" = NOT ""HasBody"";");

            migrationBuilder.DropColumn(
                name: "HasBody",
                table: "ConversionJobs");
        }
    }
}
