using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Rc12Tenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "chk_organisations_status",
                table: "organisations");

            migrationBuilder.DropIndex(
                name: "ux_org_memberships_active",
                table: "org_memberships");

            // rc.12 (decision of 14 Oct 2026): a membership is in its own organisation only. Before the switch goes, every
            // live membership that applied below is written out as one explicit membership per organisation below it, so
            // nobody loses access they have today. (org_admin memberships become admin rights instead; see the end.)
            migrationBuilder.Sql("""
                INSERT INTO org_memberships (id, user_id, org_id, app_id, role_id, granted_by_user_id, granted_at, revoked_at, expires_at, revoked_by_user_id)
                SELECT DISTINCT ON (m.user_id, d.id, m.app_id, m.role_id)
                       gen_random_uuid(), m.user_id, d.id, m.app_id, m.role_id, m.granted_by_user_id, m.granted_at, NULL, m.expires_at, NULL
                FROM org_memberships m
                JOIN organisations p ON p.id = m.org_id
                JOIN organisations d ON d.path LIKE p.path || '%' AND d.id <> p.id AND d.deleted_at IS NULL
                JOIN roles r ON r.id = m.role_id
                WHERE m.applies_to_descendants AND m.revoked_at IS NULL AND r.code <> 'org_admin'
                  AND NOT EXISTS (
                      SELECT 1 FROM org_memberships x
                      WHERE x.user_id = m.user_id AND x.org_id = d.id AND x.app_id = m.app_id AND x.role_id = m.role_id AND x.revoked_at IS NULL)
                ORDER BY m.user_id, d.id, m.app_id, m.role_id, m.granted_at;
                """);

            migrationBuilder.DropColumn(
                name: "applies_to_descendants",
                table: "org_memberships");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "locked_at",
                table: "user_sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "shared_device_id",
                table: "user_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "closed_at",
                table: "organisations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "device_full_sign_in_hours",
                table: "organisations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "device_pin_minutes",
                table: "organisations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "emergency_access",
                table: "organisations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "roles_kept_in_sangam",
                table: "organisations",
                type: "boolean",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "role_id",
                table: "org_memberships",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "add_without_digi_locker_until",
                table: "apps",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "last_org_id",
                table: "app_grants",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "role_id",
                table: "access_review_decisions",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateTable(
                name: "emergency_accesses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    case_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    end_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    verdict = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    review_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_emergency_accesses", x => x.id);
                    table.ForeignKey(
                        name: "fk_emergency_accesses_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_emergency_accesses_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_emergency_accesses_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "join_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    used_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_join_codes", x => x.id);
                    table.ForeignKey(
                        name: "fk_join_codes_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "org_admins",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    right = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    granted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_org_admins", x => x.id);
                    table.ForeignKey(
                        name: "fk_org_admins_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_org_admins_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_org_admins_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "shared_devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    enrol_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    enrol_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    enrolled_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    enrolled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shared_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_shared_devices_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_shared_devices_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "staff_checks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    admin_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    differences = table.Column<string>(type: "jsonb", nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staff_checks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "staff_invites",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    last_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    normalized_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    mobile = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    date_of_birth = table.Column<DateOnly>(type: "date", nullable: false),
                    existing_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    accepted_differences = table.Column<string>(type: "jsonb", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    code_hash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    failed_codes = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    added_before_digi_locker = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    accepted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staff_invites", x => x.id);
                    table.ForeignKey(
                        name: "fk_staff_invites_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_pins",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pin_hash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    set_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    failed_count = table.Column<int>(type: "integer", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_pins", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_user_pins_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "shared_device_people",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_full_sign_in_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sign_in_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    methods = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    second_factor = table.Column<bool>(type: "boolean", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    pin_offered_session_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shared_device_people", x => x.id);
                    table.ForeignKey(
                        name: "fk_shared_device_people_shared_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "shared_devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_shared_device_people_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pending_memberships",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invite_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: false),
                    roles = table.Column<string>(type: "jsonb", nullable: false),
                    starts_on = table.Column<DateOnly>(type: "date", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pending_memberships", x => x.id);
                    table.ForeignKey(
                        name: "fk_pending_memberships_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_pending_memberships_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_pending_memberships_staff_invites_invite_id",
                        column: x => x.invite_id,
                        principalTable: "staff_invites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "chk_organisations_status",
                table: "organisations",
                sql: "status IN ('active','suspended','deleted_soft','deleted_hard','closed')");

            migrationBuilder.CreateIndex(
                name: "ux_org_memberships_active",
                table: "org_memberships",
                columns: new[] { "user_id", "org_id", "app_id", "role_id" },
                unique: true,
                filter: "revoked_at IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "idx_emergency_accesses_org",
                table: "emergency_accesses",
                columns: new[] { "org_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "idx_emergency_accesses_user",
                table: "emergency_accesses",
                columns: new[] { "user_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_emergency_accesses_app_id",
                table: "emergency_accesses",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "idx_join_codes_live",
                table: "join_codes",
                column: "code_hash",
                filter: "used_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_join_codes_user_id",
                table: "join_codes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "idx_org_admins_user",
                table: "org_admins",
                column: "user_id",
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_org_admins_org_id",
                table: "org_admins",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "ux_org_admins_active",
                table: "org_admins",
                columns: new[] { "app_id", "org_id", "user_id", "right" },
                unique: true,
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "idx_pending_memberships_invite",
                table: "pending_memberships",
                column: "invite_id");

            migrationBuilder.CreateIndex(
                name: "idx_pending_memberships_waiting",
                table: "pending_memberships",
                columns: new[] { "user_id", "starts_on" },
                filter: "applied_at IS NULL AND cancelled_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_pending_memberships_app_id",
                table: "pending_memberships",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "ix_pending_memberships_org_id",
                table: "pending_memberships",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "idx_shared_device_people_recent",
                table: "shared_device_people",
                columns: new[] { "device_id", "last_seen_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_shared_device_people_user_id",
                table: "shared_device_people",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_shared_device_people",
                table: "shared_device_people",
                columns: new[] { "device_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_shared_devices_org",
                table: "shared_devices",
                columns: new[] { "app_id", "org_id" });

            migrationBuilder.CreateIndex(
                name: "ix_shared_devices_org_id",
                table: "shared_devices",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "ux_shared_devices_enrol",
                table: "shared_devices",
                column: "enrol_token_hash",
                unique: true,
                filter: "enrol_token_hash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_shared_devices_key",
                table: "shared_devices",
                column: "key_hash",
                unique: true,
                filter: "key_hash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "idx_staff_checks_admin",
                table: "staff_checks",
                columns: new[] { "admin_user_id", "subject_hash", "checked_at" });

            migrationBuilder.CreateIndex(
                name: "idx_staff_invites_email",
                table: "staff_invites",
                column: "normalized_email");

            migrationBuilder.CreateIndex(
                name: "ix_staff_invites_app_id",
                table: "staff_invites",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "ux_staff_invites_token",
                table: "staff_invites",
                column: "token_hash",
                unique: true);

            // rc.12: whoever holds the org_admin role gets an admin right over that organisation and everything below it
            // (kept apart from roles), starting from when the role was given. The role itself is left as it is: an
            // application that checks it keeps working, and it grants nothing in Sangam any more.
            migrationBuilder.Sql("""
                INSERT INTO org_admins (id, app_id, org_id, user_id, "right", granted_by_user_id, granted_at)
                SELECT DISTINCT ON (m.app_id, m.org_id, m.user_id)
                       gen_random_uuid(), m.app_id, m.org_id, m.user_id, 'admin', m.granted_by_user_id, m.granted_at
                FROM org_memberships m
                JOIN roles r ON r.id = m.role_id
                WHERE r.code = 'org_admin' AND m.revoked_at IS NULL AND (m.expires_at IS NULL OR m.expires_at > now())
                ORDER BY m.app_id, m.org_id, m.user_id, m.granted_at;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "emergency_accesses");

            migrationBuilder.DropTable(
                name: "join_codes");

            migrationBuilder.DropTable(
                name: "org_admins");

            migrationBuilder.DropTable(
                name: "pending_memberships");

            migrationBuilder.DropTable(
                name: "shared_device_people");

            migrationBuilder.DropTable(
                name: "staff_checks");

            migrationBuilder.DropTable(
                name: "user_pins");

            migrationBuilder.DropTable(
                name: "staff_invites");

            migrationBuilder.DropTable(
                name: "shared_devices");

            migrationBuilder.DropCheckConstraint(
                name: "chk_organisations_status",
                table: "organisations");

            migrationBuilder.DropIndex(
                name: "ux_org_memberships_active",
                table: "org_memberships");

            migrationBuilder.DropColumn(
                name: "locked_at",
                table: "user_sessions");

            migrationBuilder.DropColumn(
                name: "shared_device_id",
                table: "user_sessions");

            migrationBuilder.DropColumn(
                name: "closed_at",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "device_full_sign_in_hours",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "device_pin_minutes",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "emergency_access",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "roles_kept_in_sangam",
                table: "organisations");

            migrationBuilder.DropColumn(
                name: "add_without_digi_locker_until",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "last_org_id",
                table: "app_grants");

            migrationBuilder.AlterColumn<Guid>(
                name: "role_id",
                table: "org_memberships",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "applies_to_descendants",
                table: "org_memberships",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<Guid>(
                name: "role_id",
                table: "access_review_decisions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "chk_organisations_status",
                table: "organisations",
                sql: "status IN ('active','suspended','deleted_soft','deleted_hard')");

            migrationBuilder.CreateIndex(
                name: "ux_org_memberships_active",
                table: "org_memberships",
                columns: new[] { "user_id", "org_id", "app_id" },
                unique: true,
                filter: "revoked_at IS NULL");
        }
    }
}
