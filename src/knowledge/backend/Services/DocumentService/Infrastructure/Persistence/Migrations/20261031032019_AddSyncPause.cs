using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentService.Infrastructure.Migrations
{
    /// <summary>
    /// FR-20, SC-20 主要素 7, 計画 ADR-0145 決定 1・3, [[IADR-0667]] (#2378): 同期設定に**利用者単位の一時停止**（停止期間の始まりと識別子）を、
    /// 同期の監査ログに**停止期間ごとの拒否の 1 行**の列（停止期間・拒否回数・最後の拒否の時刻）を足す。既存の行は「有効」「停止期間なし」のまま
    /// （行の書き換えは無い）。起動時の <c>MigrateAsync</c> が当てる（<c>Program.cs</c>）。
    /// </summary>
    public partial class AddSyncPause : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PausePeriodId",
                table: "SyncSettings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PausedAt",
                table: "SyncSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastRejectedAt",
                table: "SyncAuditEntries",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PausePeriodId",
                table: "SyncAuditEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RejectedCount",
                table: "SyncAuditEntries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_SyncAuditEntries_OwnerId_PausePeriodId",
                table: "SyncAuditEntries",
                columns: new[] { "OwnerId", "PausePeriodId" },
                unique: true,
                filter: "\"PausePeriodId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SyncAuditEntries_OwnerId_PausePeriodId",
                table: "SyncAuditEntries");

            migrationBuilder.DropColumn(
                name: "PausePeriodId",
                table: "SyncSettings");

            migrationBuilder.DropColumn(
                name: "PausedAt",
                table: "SyncSettings");

            migrationBuilder.DropColumn(
                name: "LastRejectedAt",
                table: "SyncAuditEntries");

            migrationBuilder.DropColumn(
                name: "PausePeriodId",
                table: "SyncAuditEntries");

            migrationBuilder.DropColumn(
                name: "RejectedCount",
                table: "SyncAuditEntries");
        }
    }
}
