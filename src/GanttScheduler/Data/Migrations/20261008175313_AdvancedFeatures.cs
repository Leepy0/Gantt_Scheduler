using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GanttScheduler.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdvancedFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Dept",
                schema: "pm",
                table: "Users");

            migrationBuilder.AddColumn<DateOnly>(
                name: "BaselineEnd",
                schema: "pm",
                table: "WorkItems",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "BaselineStart",
                schema: "pm",
                table: "WorkItems",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                schema: "pm",
                table: "WorkItems",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "OrgUnitId",
                schema: "pm",
                table: "Users",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "BaselineDate",
                schema: "pm",
                table: "Stages",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                schema: "pm",
                table: "Stages",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "BaselineAt",
                schema: "pm",
                table: "Projects",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                schema: "pm",
                table: "Projects",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Attachments",
                schema: "pm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    StageId = table.Column<int>(type: "int", nullable: true),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    StoredName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    UploadedById = table.Column<int>(type: "int", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Attachments_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "pm",
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Attachments_Users_UploadedById",
                        column: x => x.UploadedById,
                        principalSchema: "pm",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CheckItems",
                schema: "pm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StageId = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsDone = table.Column<bool>(type: "bit", nullable: false),
                    DoneById = table.Column<int>(type: "int", nullable: true),
                    DoneAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CheckItems_Stages_StageId",
                        column: x => x.StageId,
                        principalSchema: "pm",
                        principalTable: "Stages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CheckItems_Users_DoneById",
                        column: x => x.DoneById,
                        principalSchema: "pm",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrgUnits",
                schema: "pm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ParentId = table.Column<int>(type: "int", nullable: true),
                    Level = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgUnits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrgUnits_OrgUnits_ParentId",
                        column: x => x.ParentId,
                        principalSchema: "pm",
                        principalTable: "OrgUnits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TemplateCheckItems",
                schema: "pm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StageTemplateId = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemplateCheckItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TemplateCheckItems_StageTemplates_StageTemplateId",
                        column: x => x.StageTemplateId,
                        principalSchema: "pm",
                        principalTable: "StageTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkLinks",
                schema: "pm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    PredecessorId = table.Column<int>(type: "int", nullable: false),
                    SuccessorId = table.Column<int>(type: "int", nullable: false),
                    LagDays = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkLinks_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalSchema: "pm",
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkLinks_WorkItems_PredecessorId",
                        column: x => x.PredecessorId,
                        principalSchema: "pm",
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkLinks_WorkItems_SuccessorId",
                        column: x => x.SuccessorId,
                        principalSchema: "pm",
                        principalTable: "WorkItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_OrgUnitId",
                schema: "pm",
                table: "Users",
                column: "OrgUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_ProjectId",
                schema: "pm",
                table: "Attachments",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_UploadedById",
                schema: "pm",
                table: "Attachments",
                column: "UploadedById");

            migrationBuilder.CreateIndex(
                name: "IX_CheckItems_DoneById",
                schema: "pm",
                table: "CheckItems",
                column: "DoneById");

            migrationBuilder.CreateIndex(
                name: "IX_CheckItems_StageId",
                schema: "pm",
                table: "CheckItems",
                column: "StageId");

            migrationBuilder.CreateIndex(
                name: "IX_OrgUnits_ParentId",
                schema: "pm",
                table: "OrgUnits",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_TemplateCheckItems_StageTemplateId",
                schema: "pm",
                table: "TemplateCheckItems",
                column: "StageTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkLinks_PredecessorId_SuccessorId",
                schema: "pm",
                table: "WorkLinks",
                columns: new[] { "PredecessorId", "SuccessorId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkLinks_ProjectId",
                schema: "pm",
                table: "WorkLinks",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkLinks_SuccessorId",
                schema: "pm",
                table: "WorkLinks",
                column: "SuccessorId");

            migrationBuilder.AddForeignKey(
                name: "FK_Users_OrgUnits_OrgUnitId",
                schema: "pm",
                table: "Users",
                column: "OrgUnitId",
                principalSchema: "pm",
                principalTable: "OrgUnits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_OrgUnits_OrgUnitId",
                schema: "pm",
                table: "Users");

            migrationBuilder.DropTable(
                name: "Attachments",
                schema: "pm");

            migrationBuilder.DropTable(
                name: "CheckItems",
                schema: "pm");

            migrationBuilder.DropTable(
                name: "OrgUnits",
                schema: "pm");

            migrationBuilder.DropTable(
                name: "TemplateCheckItems",
                schema: "pm");

            migrationBuilder.DropTable(
                name: "WorkLinks",
                schema: "pm");

            migrationBuilder.DropIndex(
                name: "IX_Users_OrgUnitId",
                schema: "pm",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "BaselineEnd",
                schema: "pm",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "BaselineStart",
                schema: "pm",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "pm",
                table: "WorkItems");

            migrationBuilder.DropColumn(
                name: "OrgUnitId",
                schema: "pm",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "BaselineDate",
                schema: "pm",
                table: "Stages");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "pm",
                table: "Stages");

            migrationBuilder.DropColumn(
                name: "BaselineAt",
                schema: "pm",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "pm",
                table: "Projects");

            migrationBuilder.AddColumn<string>(
                name: "Dept",
                schema: "pm",
                table: "Users",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);
        }
    }
}
