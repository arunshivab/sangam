using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProfileFieldsSignInModesAndOneTimeCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-edited: existing rows take the model defaults ("prefer_not_to_say", "password", "default")
            // instead of EF's "" so the CHECK constraints added below hold on a populated table.
            migrationBuilder.DropColumn(
                name: "name",
                table: "users");

            migrationBuilder.AddColumn<DateOnly>(
                name: "date_of_birth",
                table: "users",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<string>(
                name: "first_name",
                table: "users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "gender",
                table: "users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "prefer_not_to_say");

            migrationBuilder.AddColumn<string>(
                name: "last_name",
                table: "users",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "sign_in_preference",
                table: "users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "password");

            migrationBuilder.AddColumn<string>(
                name: "sign_in_policy",
                table: "apps",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "default");

            migrationBuilder.CreateTable(
                name: "one_time_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_one_time_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_one_time_codes_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "chk_users_gender",
                table: "users",
                sql: "gender IN ('female','male','other','prefer_not_to_say')");

            migrationBuilder.AddCheckConstraint(
                name: "chk_users_sign_in_preference",
                table: "users",
                sql: "sign_in_preference IN ('password','password_and_otp','otp_only')");

            migrationBuilder.AddCheckConstraint(
                name: "chk_apps_sign_in_policy",
                table: "apps",
                sql: "sign_in_policy IN ('default','password','password_and_otp','otp_only')");

            migrationBuilder.CreateIndex(
                name: "idx_one_time_codes_user_purpose_created",
                table: "one_time_codes",
                columns: new[] { "user_id", "purpose", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "one_time_codes");

            migrationBuilder.DropCheckConstraint(
                name: "chk_users_gender",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "chk_users_sign_in_preference",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "chk_apps_sign_in_policy",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "date_of_birth",
                table: "users");

            migrationBuilder.DropColumn(
                name: "first_name",
                table: "users");

            migrationBuilder.DropColumn(
                name: "gender",
                table: "users");

            migrationBuilder.DropColumn(
                name: "last_name",
                table: "users");

            migrationBuilder.DropColumn(
                name: "sign_in_preference",
                table: "users");

            migrationBuilder.DropColumn(
                name: "sign_in_policy",
                table: "apps");

            migrationBuilder.AddColumn<string>(
                name: "name",
                table: "users",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");
        }
    }
}
