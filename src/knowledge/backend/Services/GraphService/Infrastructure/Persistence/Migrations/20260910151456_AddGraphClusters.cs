using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GraphService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGraphClusters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "graph_clusters",
                columns: table => new
                {
                    ClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompositionChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MemberCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_graph_clusters", x => x.ClusterId);
                });

            migrationBuilder.CreateTable(
                name: "graph_cluster_members",
                columns: table => new
                {
                    ClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_graph_cluster_members", x => new { x.ClusterId, x.DocumentId });
                    table.ForeignKey(
                        name: "FK_graph_cluster_members_graph_clusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "graph_clusters",
                        principalColumn: "ClusterId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "graph_cluster_summaries",
                columns: table => new
                {
                    ClusterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Confidentiality = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_graph_cluster_summaries", x => new { x.ClusterId, x.Confidentiality });
                    table.ForeignKey(
                        name: "FK_graph_cluster_summaries_graph_clusters_ClusterId",
                        column: x => x.ClusterId,
                        principalTable: "graph_clusters",
                        principalColumn: "ClusterId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_graph_cluster_members_document",
                table: "graph_cluster_members",
                column: "DocumentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "graph_cluster_members");

            migrationBuilder.DropTable(
                name: "graph_cluster_summaries");

            migrationBuilder.DropTable(
                name: "graph_clusters");
        }
    }
}
