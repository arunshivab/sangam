using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UnknownAddressAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "unknown_address_attempts",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    failures = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unknown_address_attempts", x => x.key);
                });

            migrationBuilder.CreateIndex(
                name: "idx_unknown_address_attempts_last_seen",
                table: "unknown_address_attempts",
                column: "last_seen");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "unknown_address_attempts");
        }
    }
}
