using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlatformApps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_platform",
                table: "apps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Sangam's own clients, wherever they were registered before this column existed.
            // Literal ids on purpose: a migration must not change meaning if a constant is renamed.
            migrationBuilder.Sql(
                "UPDATE apps SET is_platform = TRUE WHERE client_id IN ('sangam-portal', 'sangam-admin', 'sangam-partner');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_platform",
                table: "apps");
        }
    }
}
