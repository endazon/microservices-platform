using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentService.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// FR-20, UC-11, SC-20 主要素 6, ADR-0037 決定 9, ADR-0099 決定 1・3・5, IADR-0446 (#1446):
    /// 同期の監査ログ（SyncAuditEntries）に**貯蔵**を与える 1 表を足す。
    ///
    /// **追加だけで、既存行の書き換えは無い。** 既存の同期の記録は構造化ログにしか存在せず、
    /// backfill できない（ログから行を起こさない）—— **移行の時点より前の同期履歴は空である**。
    /// これは ADR-0099 決定 3（3 年保持）に反しない: 保持は「これから記録するもの」の期限である。
    ///
    /// 🔴 **資料を指す列が無い**（決定 5）。DocumentId・タイトル・VaultPath の列は
    /// 意図的に存在しない —— 題名を 3 年残さないという決定を、書ける場所を作らないことで守る。
    ///
    /// 🔴 **FK が無い**（端末・資料が消えても行は残る＝監査ログ）。削除は保持期限の定期処理だけが行う。
    /// </summary>
    public partial class AddSyncAuditEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SyncAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Added = table.Column<int>(type: "integer", nullable: false),
                    Updated = table.Column<int>(type: "integer", nullable: false),
                    Deleted = table.Column<int>(type: "integer", nullable: false),
                    Conflicted = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FailureReason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncAuditEntries_OwnerId_OccurredAt",
                table: "SyncAuditEntries",
                columns: new[] { "OwnerId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncAuditEntries");
        }
    }
}
