using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Rc6Policies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "inactivity_notice_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_sign_in_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "record",
                table: "mfa_reset_requests",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_reason",
                table: "mfa_reset_requests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "review_status",
                table: "mfa_reset_requests",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reviewed_at",
                table: "mfa_reset_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reviewed_by_user_id",
                table: "mfa_reset_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "inactivity_limit_years",
                table: "apps",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "require_identity_verification",
                table: "apps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "inactivity_notice_at",
                table: "app_grants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_used_at",
                table: "app_grants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_users_last_sign_in",
                table: "users",
                column: "last_sign_in_at");

            migrationBuilder.CreateIndex(
                name: "idx_mfa_reset_requests_review",
                table: "mfa_reset_requests",
                column: "requested_at",
                filter: "review_status = 'waiting' AND cancelled_at IS NULL AND applied_at IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "chk_mfa_reset_requests_review_status",
                table: "mfa_reset_requests",
                sql: "review_status IS NULL OR review_status IN ('waiting', 'approved', 'refused')");

            migrationBuilder.AddCheckConstraint(
                name: "chk_apps_inactivity_limit_years",
                table: "apps",
                sql: "inactivity_limit_years IS NULL OR inactivity_limit_years BETWEEN 1 AND 10");

            // rc.6 (SGM-910 section 7): nobody is inactive on the day this is deployed. Existing connections and accounts
            // count from now; the clocks run from the next use.
            migrationBuilder.Sql("UPDATE app_grants SET last_used_at = now() WHERE revoked_at IS NULL AND last_used_at IS NULL;");
            migrationBuilder.Sql("UPDATE users SET last_sign_in_at = now() WHERE last_sign_in_at IS NULL;");

            // rc.6 (SGM-910 section 6): a closed grievance is kept three years, then deleted (the founder's decision of
            // 10 Oct 2026 replaces D-D's "never deleted"). The history stays protected against any other change: only the
            // retention sweep, in a transaction that sets sangam.retention_maintenance, may delete.
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION grievance_history_protect() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' AND current_setting('sangam.retention_maintenance', true) = 'on' THEN
        RETURN OLD;
    END IF;
    RAISE EXCEPTION '% is append-only: % is not allowed', TG_TABLE_NAME, TG_OP USING ERRCODE = 'insufficient_privilege';
END
$$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION grievance_history_protect() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% is append-only: % is not allowed', TG_TABLE_NAME, TG_OP USING ERRCODE = 'insufficient_privilege';
END
$$;");

            migrationBuilder.DropIndex(
                name: "idx_users_last_sign_in",
                table: "users");

            migrationBuilder.DropIndex(
                name: "idx_mfa_reset_requests_review",
                table: "mfa_reset_requests");

            migrationBuilder.DropCheckConstraint(
                name: "chk_mfa_reset_requests_review_status",
                table: "mfa_reset_requests");

            migrationBuilder.DropCheckConstraint(
                name: "chk_apps_inactivity_limit_years",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "inactivity_notice_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "last_sign_in_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "record",
                table: "mfa_reset_requests");

            migrationBuilder.DropColumn(
                name: "review_reason",
                table: "mfa_reset_requests");

            migrationBuilder.DropColumn(
                name: "review_status",
                table: "mfa_reset_requests");

            migrationBuilder.DropColumn(
                name: "reviewed_at",
                table: "mfa_reset_requests");

            migrationBuilder.DropColumn(
                name: "reviewed_by_user_id",
                table: "mfa_reset_requests");

            migrationBuilder.DropColumn(
                name: "inactivity_limit_years",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "require_identity_verification",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "inactivity_notice_at",
                table: "app_grants");

            migrationBuilder.DropColumn(
                name: "last_used_at",
                table: "app_grants");
        }
    }
}
