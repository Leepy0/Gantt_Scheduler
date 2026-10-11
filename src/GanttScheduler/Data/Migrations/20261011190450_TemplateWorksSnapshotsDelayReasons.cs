using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GanttScheduler.Data.Migrations
{
    /// <inheritdoc />
    public partial class TemplateWorksSnapshotsDelayReasons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReasonCode",
                schema: "pm",
                table: "Activities",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DelayReasons",
                schema: "pm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DelayReasons", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectSnapshots",
                schema: "pm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Progress = table.Column<int>(type: "int", nullable: false),
                    DelayDays = table.Column<int>(type: "int", nullable: false),
                    RiskDays = table.Column<int>(type: "int", nullable: false),
                    AtRisk = table.Column<bool>(type: "bit", nullable: false),
                    EndTarget = table.Column<DateOnly>(type: "date", nullable: true),
                    SlipDays = table.Column<int>(type: "int", nullable: true),
                    CurrentStage = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CategoryId = table.Column<int>(type: "int", nullable: false),
                    CorporationId = table.Column<int>(type: "int", nullable: false),
                    OwnerId = table.Column<int>(type: "int", nullable: false),
                    OrgUnitId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectSnapshots_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "pm",
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TemplateWorks",
                schema: "pm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StageTemplateId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OffsetWorkDays = table.Column<int>(type: "int", nullable: false),
                    DurationWorkDays = table.Column<int>(type: "int", nullable: false),
                    PredecessorTemplateWorkId = table.Column<int>(type: "int", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemplateWorks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TemplateWorks_StageTemplates_StageTemplateId",
                        column: x => x.StageTemplateId,
                        principalSchema: "pm",
                        principalTable: "StageTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TemplateWorks_TemplateWorks_PredecessorTemplateWorkId",
                        column: x => x.PredecessorTemplateWorkId,
                        principalSchema: "pm",
                        principalTable: "TemplateWorks",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectSnapshots_Date",
                schema: "pm",
                table: "ProjectSnapshots",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectSnapshots_ProjectId_Date",
                schema: "pm",
                table: "ProjectSnapshots",
                columns: new[] { "ProjectId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TemplateWorks_PredecessorTemplateWorkId",
                schema: "pm",
                table: "TemplateWorks",
                column: "PredecessorTemplateWorkId");

            migrationBuilder.CreateIndex(
                name: "IX_TemplateWorks_StageTemplateId",
                schema: "pm",
                table: "TemplateWorks",
                column: "StageTemplateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DelayReasons",
                schema: "pm");

            migrationBuilder.DropTable(
                name: "ProjectSnapshots",
                schema: "pm");

            migrationBuilder.DropTable(
                name: "TemplateWorks",
                schema: "pm");

            migrationBuilder.DropColumn(
                name: "ReasonCode",
                schema: "pm",
                table: "Activities");
        }
    }
}
