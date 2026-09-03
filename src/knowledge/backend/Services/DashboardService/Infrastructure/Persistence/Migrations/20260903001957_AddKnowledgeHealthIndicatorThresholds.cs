using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DashboardService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKnowledgeHealthIndicatorThresholds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FR-10, SC-10, planning#494 決定 3, IADR-0353 (#1186): 指標ごとの現在のしきい値。
            // 🔴 **観測値の表とは別に持つ。** 観測値は全量スナップショットであり
            // **件数 0 のときは 1 行も無い**。そこへ持たせるとしきい値も一緒に消え、
            // 計画が求めた「件数と現在のしきい値を併記する」が 0 件のときにだけ満たせなくなる。
            migrationBuilder.CreateTable(
                name: "KnowledgeHealthIndicatorThresholds",
                columns: table => new
                {
                    Indicator = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ThresholdDays = table.Column<int>(type: "integer", nullable: false),
                    ReportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeHealthIndicatorThresholds", x => x.Indicator);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KnowledgeHealthIndicatorThresholds");
        }
    }
}
