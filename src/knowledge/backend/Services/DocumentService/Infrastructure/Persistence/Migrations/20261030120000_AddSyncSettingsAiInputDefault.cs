using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentService.Infrastructure.Migrations
{
    /// <summary>
    /// FR-19, SC-20 主要素 8, 計画 ADR-0143 決定 1 (#2120): 同期設定に AI 入力の利用者単位の既定を足す。
    /// **既存の行は false**（未設定と OFF を区別しない）。行の書き換えは無い。起動時の <c>MigrateAsync</c> が当てる（<c>Program.cs</c>）。
    /// </summary>
    public partial class AddSyncSettingsAiInputDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AiInputDefault",
                table: "SyncSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiInputDefault",
                table: "SyncSettings");
        }
    }
}
