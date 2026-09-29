using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppAdminRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-edited: any existing app admin becomes "admin", the lesser rank, so nobody is
            // silently promoted to owner by a migration.
            migrationBuilder.AddColumn<string>(
                name: "role",
                table: "app_admins",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "admin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "role",
                table: "app_admins");
        }
    }
}
