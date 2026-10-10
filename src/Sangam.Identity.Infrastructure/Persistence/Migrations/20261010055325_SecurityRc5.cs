using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SecurityRc5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "chk_organisations_min_password_length",
                table: "organisations");

            migrationBuilder.DropCheckConstraint(
                name: "chk_apps_min_password_length",
                table: "apps");

            migrationBuilder.AddColumn<bool>(
                name: "require_character_types",
                table: "organisations",
                type: "boolean",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "invited_by_user_id",
                table: "invitations",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<bool>(
                name: "require_character_types",
                table: "apps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // rc.5 (ASVS V2.1.1): the floor is 12 characters. A level that asked for less now follows the floor.
            migrationBuilder.Sql("UPDATE organisations SET min_password_length = 12 WHERE min_password_length < 12;");
            migrationBuilder.Sql("UPDATE apps SET min_password_length = 12 WHERE min_password_length < 12;");

            migrationBuilder.AddCheckConstraint(
                name: "chk_organisations_min_password_length",
                table: "organisations",
                sql: "min_password_length IS NULL OR min_password_length BETWEEN 12 AND 64");

            migrationBuilder.AddCheckConstraint(
                name: "chk_apps_min_password_length",
                table: "apps",
                sql: "min_password_length IS NULL OR min_password_length BETWEEN 12 AND 64");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "chk_organisations_min_password_length",
                table: "organisations");

            migrationBuilder.DropCheckConstraint(
                name: "chk_apps_min_password_length",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "require_character_types",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "require_character_types",
                table: "apps");

            migrationBuilder.AlterColumn<Guid>(
                name: "invited_by_user_id",
                table: "invitations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "chk_organisations_min_password_length",
                table: "organisations",
                sql: "min_password_length IS NULL OR min_password_length BETWEEN 8 AND 64");

            migrationBuilder.AddCheckConstraint(
                name: "chk_apps_min_password_length",
                table: "apps",
                sql: "min_password_length IS NULL OR min_password_length BETWEEN 8 AND 64");
        }
    }
}
