using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GanttScheduler.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class ParallelStagesAutoTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Parallel",
                table: "StageTemplates",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AutoTarget",
                table: "Stages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Parallel",
                table: "Stages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Parallel",
                table: "StageTemplates");

            migrationBuilder.DropColumn(
                name: "AutoTarget",
                table: "Stages");

            migrationBuilder.DropColumn(
                name: "Parallel",
                table: "Stages");
        }
    }
}
