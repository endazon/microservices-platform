using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentService.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// FR-20, UC-11, SC-20 主要素 3・5, ADR-0037 決定 3・4・7, IADR-0352 (#1442):
    /// 同期対象範囲（SyncSettings）と同期競合（SyncConflicts）の 2 表を足す。
    ///
    /// **追加だけで、既存行の書き換えは無い。** 同期設定が無い利用者は従来どおり
    /// 「全資料が対象」（決定 3 の既定）であり、行が無いことがその意味である ——
    /// 空の行を backfill しない（作った覚えのない設定が画面に現れる）。
    ///
    /// 競合は資料（PrivateNotes）へ Cascade で従う。資料を完全削除すれば競合も消える ——
    /// 資料が無い競合は、どの 3 択も適用できない。
    ///
    /// 🔴 **ローカル本文はこの表に入らない。** オブジェクトストレージへ置き、参照 URI だけを持つ
    /// （台帳が本文を持たないという PrivateNote の設計と揃える）。
    /// </summary>
    public partial class AddSyncSettingsAndConflicts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SyncConflicts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocalBaseVersion = table.Column<int>(type: "integer", nullable: false),
                    ServerVersion = table.Column<int>(type: "integer", nullable: false),
                    LocalContentUri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Resolution = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncConflicts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SyncConflicts_PrivateNotes_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "PrivateNotes",
                        principalColumn: "DocumentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncSettings",
                columns: table => new
                {
                    OwnerId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TargetFolders = table.Column<string>(type: "jsonb", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncSettings", x => x.OwnerId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflicts_DocumentId",
                table: "SyncConflicts",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncConflicts_OwnerId_ResolvedAt",
                table: "SyncConflicts",
                columns: new[] { "OwnerId", "ResolvedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncConflicts");

            migrationBuilder.DropTable(
                name: "SyncSettings");
        }
    }
}
