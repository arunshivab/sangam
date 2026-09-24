using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppBranding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-edited: existing apps take the model defaults so the seeded sample app stays valid.
            migrationBuilder.AddColumn<string>(
                name: "brand_colour",
                table: "apps",
                type: "character varying(9)",
                maxLength: 9,
                nullable: false,
                defaultValue: "#0F3B38");

            migrationBuilder.AddColumn<string>(
                name: "consent_version",
                table: "apps",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "v1");

            migrationBuilder.AddColumn<string>(
                name: "glyph",
                table: "apps",
                type: "character varying(4)",
                maxLength: 4,
                nullable: false,
                defaultValue: "?");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "brand_colour",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "consent_version",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "glyph",
                table: "apps");
        }
    }
}
