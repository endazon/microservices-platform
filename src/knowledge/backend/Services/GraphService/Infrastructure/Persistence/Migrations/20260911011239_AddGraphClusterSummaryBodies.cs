using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GraphService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGraphClusterSummaryBodies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "graph_cluster_summary_bodies",
                columns: table => new
                {
                    ClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Confidentiality = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_graph_cluster_summary_bodies", x => new { x.ClusterId, x.Confidentiality });
                    table.ForeignKey(
                        name: "FK_graph_cluster_summary_bodies_graph_clusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "graph_clusters",
                        principalColumn: "ClusterId",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "graph_cluster_summary_bodies");
        }
    }
}
