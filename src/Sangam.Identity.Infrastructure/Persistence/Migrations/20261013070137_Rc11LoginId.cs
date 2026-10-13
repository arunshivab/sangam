using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Rc11LoginId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "login_id",
                table: "users",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "login_id_releases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    login_id = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_login_id_releases", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_users_login_id",
                table: "users",
                column: "login_id",
                unique: true,
                filter: "login_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "idx_login_id_releases_login_id",
                table: "login_id_releases",
                columns: new[] { "login_id", "released_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "login_id_releases");

            migrationBuilder.DropIndex(
                name: "ux_users_login_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "login_id",
                table: "users");
        }
    }
}
