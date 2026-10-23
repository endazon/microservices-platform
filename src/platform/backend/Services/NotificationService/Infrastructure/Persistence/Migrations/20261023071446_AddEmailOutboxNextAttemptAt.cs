using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NotificationService.Infrastructure.Migrations
{
    // FR-22, IADR-0600 決定 2・4: 一過性の送信失敗の再試行の次回時刻。
    // **既存行は遡及しない** —— 追加は null 許容で、既存の pending / deferred 行は null（＝いつでも拾う）
    // として従来どおり次の周期で拾われる。**既に failed の行は failed のまま**であり、再送の対象へ戻さない
    // （失敗が一過性だったかを後から判別する材料が残っていない。LastReason は理由語だけである）。
    /// <inheritdoc />
    public partial class AddEmailOutboxNextAttemptAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAt",
                table: "EmailOutbox",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "EmailOutbox");
        }
    }
}
