using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SecurityPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "chk_apps_sign_in_policy",
                table: "apps");

            migrationBuilder.AddColumn<bool>(
                name: "breached_password_check",
                table: "organisations",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mfa_requirement",
                table: "organisations",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "min_password_length",
                table: "organisations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "sign_in_policy",
                table: "organisations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "breached_password_check",
                table: "apps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "mfa_requirement",
                table: "apps",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "optional");

            migrationBuilder.AddColumn<int>(
                name: "min_password_length",
                table: "apps",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "chk_organisations_mfa_requirement",
                table: "organisations",
                sql: "mfa_requirement IS NULL OR mfa_requirement IN ('optional','required_for_administrators','required')");

            migrationBuilder.AddCheckConstraint(
                name: "chk_organisations_min_password_length",
                table: "organisations",
                sql: "min_password_length IS NULL OR min_password_length BETWEEN 8 AND 64");

            migrationBuilder.AddCheckConstraint(
                name: "chk_organisations_sign_in_policy",
                table: "organisations",
                sql: "sign_in_policy IS NULL OR sign_in_policy IN ('password_and_otp','passkey_only')");

            migrationBuilder.AddCheckConstraint(
                name: "chk_apps_mfa_requirement",
                table: "apps",
                sql: "mfa_requirement IN ('optional','required_for_administrators','required')");

            migrationBuilder.AddCheckConstraint(
                name: "chk_apps_min_password_length",
                table: "apps",
                sql: "min_password_length IS NULL OR min_password_length BETWEEN 8 AND 64");

            migrationBuilder.AddCheckConstraint(
                name: "chk_apps_sign_in_policy",
                table: "apps",
                sql: "sign_in_policy IN ('default','password','password_and_otp','otp_only','passkey_only')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "chk_organisations_mfa_requirement",
                table: "organisations");

            migrationBuilder.DropCheckConstraint(
                name: "chk_organisations_min_password_length",
                table: "organisations");

            migrationBuilder.DropCheckConstraint(
                name: "chk_organisations_sign_in_policy",
                table: "organisations");

            migrationBuilder.DropCheckConstraint(
                name: "chk_apps_mfa_requirement",
                table: "apps");

            migrationBuilder.DropCheckConstraint(
                name: "chk_apps_min_password_length",
                table: "apps");

            migrationBuilder.DropCheckConstraint(
                name: "chk_apps_sign_in_policy",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "breached_password_check",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "mfa_requirement",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "min_password_length",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "sign_in_policy",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "breached_password_check",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "mfa_requirement",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "min_password_length",
                table: "apps");

            migrationBuilder.AddCheckConstraint(
                name: "chk_apps_sign_in_policy",
                table: "apps",
                sql: "sign_in_policy IN ('default','password','password_and_otp','otp_only')");
        }
    }
}
