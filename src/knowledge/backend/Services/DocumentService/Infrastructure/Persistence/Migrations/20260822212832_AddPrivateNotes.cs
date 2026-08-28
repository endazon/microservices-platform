using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocumentService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPrivateNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PrivateNoteQuotas",
                columns: table => new
                {
                    OwnerId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LimitBytes = table.Column<long>(type: "bigint", nullable: false),
                    Warned80 = table.Column<bool>(type: "boolean", nullable: false),
                    Warned95 = table.Column<bool>(type: "boolean", nullable: false),
                    WeeklyDigestSentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrivateNoteQuotas", x => x.OwnerId);
                });

            migrationBuilder.CreateTable(
                name: "PrivateNotes",
                columns: table => new
                {
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    VaultPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    LatestBytes = table.Column<long>(type: "bigint", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IncludeInSearch = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeInGraph = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeInAi = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PurgeAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PurgeImminentNotifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrivateNotes", x => x.DocumentId);
                    table.ForeignKey(
                        name: "FK_PrivateNotes_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DeviceName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiryNotifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncDevices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PrivateNotes_OwnerId",
                table: "PrivateNotes",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncDevices_OwnerId",
                table: "SyncDevices",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncDevices_TokenHash",
                table: "SyncDevices",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrivateNoteQuotas");

            migrationBuilder.DropTable(
                name: "PrivateNotes");

            migrationBuilder.DropTable(
                name: "SyncDevices");
        }
    }
}
