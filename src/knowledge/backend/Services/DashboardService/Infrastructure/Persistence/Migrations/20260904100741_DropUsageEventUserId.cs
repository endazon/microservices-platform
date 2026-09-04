using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DashboardService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropUsageEventUserId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FR-10, SC-10, ADR-0072 決定 1, IADR-0367 (#1198): 利用イベントから利用者識別子を落とす。
            //
            // 🔴 **移送であり、既存行の UserId は失われる。復元できない。**
            // それが本決定の目的であるが（SC-10 Q27 が避けると述べた「誰がいつ何回検索したか」の
            // 記録そのものである）、**「誰が投入したか」を後から辿る手段は無くなる。**
            // 受け口の認証（RequireAuthorization）は残るため投入経路の統制は保たれる。
            // ADR-0072 §残るもの が受け入れ済みのトレードオフとして明記している。
            //
            // **行そのものは消えない。** 消えるのは列だけであり、件数は前後で変わらない。
            migrationBuilder.DropColumn(
                name: "UserId",
                table: "UsageEvents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ★ 戻せるのは**列だけ**である。既存行には空文字が入り、**失われた値は戻らない**。
            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "UsageEvents",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");
        }
    }
}
