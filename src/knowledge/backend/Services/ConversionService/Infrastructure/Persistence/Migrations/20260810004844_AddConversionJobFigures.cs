using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConversionService.Migrations
{
    /// <inheritdoc />
    public partial class AddConversionJobFigures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConversionJobFigures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    FigureId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Coded = table.Column<bool>(type: "boolean", nullable: false),
                    Language = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Code = table.Column<string>(type: "text", nullable: true),
                    ImageUri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ImageContentType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Caption = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Corrected = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CorrectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversionJobFigures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversionJobFigures_ConversionJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "ConversionJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversionJobFigures_JobId_FigureId",
                table: "ConversionJobFigures",
                columns: new[] { "JobId", "FigureId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversionJobFigures");
        }
    }
}
