using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Rc13Tenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "testers_may_join_themselves",
                table: "organisations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "verified_staff_only",
                table: "organisations",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "testers_may_join_themselves",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "verified_staff_only",
                table: "organisations");
        }
    }
}
