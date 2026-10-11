using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GanttScheduler.Data.Migrations
{
    /// <inheritdoc />
    public partial class StageDefaultWorkDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DefaultWorkDays",
                schema: "pm",
                table: "StageTemplates",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultWorkDays",
                schema: "pm",
                table: "StageTemplates");
        }
    }
}
