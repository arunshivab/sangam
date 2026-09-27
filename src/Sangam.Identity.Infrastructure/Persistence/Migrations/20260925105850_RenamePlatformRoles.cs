using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Renames the platform ranks stored in <c>platform_operators.role</c>: <c>support</c> becomes
    /// <c>viewer</c> (it was always the read-only rank, which the old name contradicted) and
    /// <c>operator</c> becomes <c>support</c> (the word for everyone in the table should not also
    /// name one rank in it). <c>owner</c> is unchanged.
    /// </summary>
    public partial class RenamePlatformRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The order is load-bearing: move "support" out of the way first, or the second
            // statement collides with rows the first has not yet renamed.
            migrationBuilder.Sql("UPDATE platform_operators SET role = 'viewer' WHERE role = 'support';");
            migrationBuilder.Sql("UPDATE platform_operators SET role = 'support' WHERE role = 'operator';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE platform_operators SET role = 'operator' WHERE role = 'support';");
            migrationBuilder.Sql("UPDATE platform_operators SET role = 'support' WHERE role = 'viewer';");
        }
    }
}
