using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GanttScheduler.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrashAndBaselineRounds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeleteReason",
                schema: "pm",
                table: "Projects",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAt",
                schema: "pm",
                table: "Projects",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeletedById",
                schema: "pm",
                table: "Projects",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Baselines",
                schema: "pm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    No = table.Column<int>(type: "int", nullable: false),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Baselines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Baselines_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "pm",
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Baselines_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "pm",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeletedProjectLogs",
                schema: "pm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DeletedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PurgedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PurgedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeletedProjectLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BaselineItems",
                schema: "pm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BaselineId = table.Column<int>(type: "int", nullable: false),
                    StageId = table.Column<int>(type: "int", nullable: true),
                    WorkItemId = table.Column<int>(type: "int", nullable: true),
                    Start = table.Column<DateOnly>(type: "date", nullable: true),
                    End = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BaselineItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BaselineItems_Baselines_BaselineId",
                        column: x => x.BaselineId,
                        principalSchema: "pm",
                        principalTable: "Baselines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_DeletedAt",
                schema: "pm",
                table: "Projects",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_DeletedById",
                schema: "pm",
                table: "Projects",
                column: "DeletedById");

            migrationBuilder.CreateIndex(
                name: "IX_BaselineItems_BaselineId",
                schema: "pm",
                table: "BaselineItems",
                column: "BaselineId");

            migrationBuilder.CreateIndex(
                name: "IX_Baselines_ProjectId_No",
                schema: "pm",
                table: "Baselines",
                columns: new[] { "ProjectId", "No" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Baselines_UserId",
                schema: "pm",
                table: "Baselines",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_DeletedProjectLogs_PurgedAt",
                schema: "pm",
                table: "DeletedProjectLogs",
                column: "PurgedAt");

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_Users_DeletedById",
                schema: "pm",
                table: "Projects",
                column: "DeletedById",
                principalSchema: "pm",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Projects_Users_DeletedById",
                schema: "pm",
                table: "Projects");

            migrationBuilder.DropTable(
                name: "BaselineItems",
                schema: "pm");

            migrationBuilder.DropTable(
                name: "DeletedProjectLogs",
                schema: "pm");

            migrationBuilder.DropTable(
                name: "Baselines",
                schema: "pm");

            migrationBuilder.DropIndex(
                name: "IX_Projects_DeletedAt",
                schema: "pm",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_Projects_DeletedById",
                schema: "pm",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "DeleteReason",
                schema: "pm",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "pm",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "DeletedById",
                schema: "pm",
                table: "Projects");
        }
    }
}
