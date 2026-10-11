using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GanttScheduler.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkTypesStageLinksActivityIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Activities_ProjectId",
                schema: "pm",
                table: "Activities");

            migrationBuilder.AddColumn<int>(
                name: "Type",
                schema: "pm",
                table: "WorkLinks",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "StageLinks",
                schema: "pm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PredecessorStageId = table.Column<int>(type: "int", nullable: false),
                    SuccessorStageId = table.Column<int>(type: "int", nullable: false),
                    LagDays = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StageLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StageLinks_Stages_PredecessorStageId",
                        column: x => x.PredecessorStageId,
                        principalSchema: "pm",
                        principalTable: "Stages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StageLinks_Stages_SuccessorStageId",
                        column: x => x.SuccessorStageId,
                        principalSchema: "pm",
                        principalTable: "Stages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ProjectId_At",
                schema: "pm",
                table: "Activities",
                columns: new[] { "ProjectId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_StageLinks_PredecessorStageId_SuccessorStageId",
                schema: "pm",
                table: "StageLinks",
                columns: new[] { "PredecessorStageId", "SuccessorStageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StageLinks_SuccessorStageId",
                schema: "pm",
                table: "StageLinks",
                column: "SuccessorStageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StageLinks",
                schema: "pm");

            migrationBuilder.DropIndex(
                name: "IX_Activities_ProjectId_At",
                schema: "pm",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "Type",
                schema: "pm",
                table: "WorkLinks");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ProjectId",
                schema: "pm",
                table: "Activities",
                column: "ProjectId");
        }
    }
}
