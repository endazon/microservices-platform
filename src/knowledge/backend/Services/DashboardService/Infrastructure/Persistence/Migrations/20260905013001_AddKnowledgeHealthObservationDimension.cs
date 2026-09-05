using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DashboardService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeHealthObservationDimension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FR-10, SC-10, IADR-0389 決定 1 (#1246): 観測値 1 件が属する**内訳の軸**。
            //
            // IADR-0265 が先送りしていた「指標 1 つ＝件数 1 つ」を解く。
            //
            // 🔴 **NULL 可のまま入れる。backfill しない。** 既存の観測値（孤立文書数・陳腐化文書数）は
            // 軸を持たない指標のものであり、既定値を書き込むと**軸を持たない指標に内訳が生える**。
            // 閲覧は「軸を持つ観測値が 1 件も無い指標には内訳を返さない」で両者を分けている。
            //
            // 観測値は 1 時間ごとに全量置換されるため、生産側が軸を送り始めれば次周期で埋まる。
            migrationBuilder.AddColumn<string>(
                name: "Dimension",
                table: "KnowledgeHealthObservations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Dimension",
                table: "KnowledgeHealthObservations");
        }
    }
}
