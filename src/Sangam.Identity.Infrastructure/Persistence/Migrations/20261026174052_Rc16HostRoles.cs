using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// rc.16 (ASVS V1.2.2): each host's own database role gets its rights (<see cref="HostRoles"/>). No schema change.
    /// Where the roles have not been made (development, tests), nothing happens.
    /// </summary>
    public partial class Rc16HostRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(HostRoles.GrantsSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The grants stay: removing them would stop the hosts. The roles themselves are the administrator's.
        }
    }
}
