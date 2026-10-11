using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GanttScheduler.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClosedOnMasterChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "ClosedOn",
                schema: "pm",
                table: "Projects",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MasterChanges",
                schema: "pm",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    EntityId = table.Column<int>(type: "int", nullable: true),
                    Text = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    OldValue = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MasterChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MasterChanges_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "pm",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_ClosedOn",
                schema: "pm",
                table: "Projects",
                column: "ClosedOn");

            migrationBuilder.CreateIndex(
                name: "IX_MasterChanges_At",
                schema: "pm",
                table: "MasterChanges",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_MasterChanges_UserId",
                schema: "pm",
                table: "MasterChanges",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MasterChanges",
                schema: "pm");

            migrationBuilder.DropIndex(
                name: "IX_Projects_ClosedOn",
                schema: "pm",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ClosedOn",
                schema: "pm",
                table: "Projects");
        }
    }
}
