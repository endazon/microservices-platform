using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GraphService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGraphDocumentBodyUpdatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FR-10, SC-10, planning#494 決定 2, IADR-0353 (#1186): 本文が変わったときだけ
            // 前進する時刻。陳腐化文書数（stale-documents）の起点である。
            //
            // 🔴 **3 段で入れる。列の既定値では入れない。**
            // AddColumn(nullable: false, defaultValue: ...) は既存行へ**その定数**を書き込み、
            // 列に DEFAULT 制約も残す。既定値は「年 1」（DateTimeOffset の既定）であり、
            // そのままだと**全既存文書が翌周期で一斉に陳腐化として報告される**。
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BodyUpdatedAt",
                table: "graph_documents",
                type: "timestamp with time zone",
                nullable: true);

            // backfill: 既存行は UpdatedAt を写す（IADR-0353 決定 2）。
            //
            // UpdatedAt は**実際の本文更新時刻以降**であるため、既存文書は実際より新しく見える
            // —— **偽陽性は出ない**（新しい文書を陳腐と数えない）。真に陳腐な文書も、遅くとも
            // 本移行から 1 しきい値（既定 180 日）以内には必ず数えられる。
            //
            // 「NULL のまま＝不明として数えない」を採らない理由: 既存文書が**本文を編集される
            // まで恒久的に母集合から外れ**、指標がほぼ 0 を返し続ける。planning#494 が名指しした
            // 「その 0 は『問題なし』と読める」失敗そのものである。
            migrationBuilder.Sql(
                """UPDATE graph_documents SET "BodyUpdatedAt" = "UpdatedAt" WHERE "BodyUpdatedAt" IS NULL;""");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "BodyUpdatedAt",
                table: "graph_documents",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BodyUpdatedAt",
                table: "graph_documents");
        }
    }
}
