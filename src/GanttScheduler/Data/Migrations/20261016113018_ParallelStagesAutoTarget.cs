using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GanttScheduler.Data.Migrations
{
    /// <inheritdoc />
    public partial class ParallelStagesAutoTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Parallel",
                schema: "pm",
                table: "StageTemplates",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AutoTarget",
                schema: "pm",
                table: "Stages",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Parallel",
                schema: "pm",
                table: "Stages",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Parallel",
                schema: "pm",
                table: "StageTemplates");

            migrationBuilder.DropColumn(
                name: "AutoTarget",
                schema: "pm",
                table: "Stages");

            migrationBuilder.DropColumn(
                name: "Parallel",
                schema: "pm",
                table: "Stages");
        }
    }
}
