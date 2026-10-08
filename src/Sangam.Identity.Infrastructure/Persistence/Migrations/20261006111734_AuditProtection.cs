using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "hash",
                table: "audit_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "prev_hash",
                table: "audit_events",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // OI-039: replace the silent append-only rules with triggers that refuse UPDATE, DELETE
            // and TRUNCATE with an error. The rules did not cover TRUNCATE at all, and silently
            // ignoring a change hid the attempt. A session that sets sangam.audit_maintenance = 'on'
            // (test fixtures; a future retention job) is let through; in production the application
            // must run as a role that does not own this table (go-live checklist, PR-10).
            migrationBuilder.Sql("DROP RULE IF EXISTS audit_no_update ON audit_events;");
            migrationBuilder.Sql("DROP RULE IF EXISTS audit_no_delete ON audit_events;");
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION audit_events_protect() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF current_setting('sangam.audit_maintenance', true) = 'on' THEN
        IF TG_LEVEL = 'STATEMENT' THEN
            RETURN NULL;
        END IF;
        IF TG_OP = 'DELETE' THEN
            RETURN OLD;
        END IF;
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'audit_events is append-only: % is not allowed', TG_OP USING ERRCODE = 'insufficient_privilege';
END
$$;");
            migrationBuilder.Sql("CREATE TRIGGER audit_events_no_update_delete BEFORE UPDATE OR DELETE ON audit_events FOR EACH ROW EXECUTE FUNCTION audit_events_protect();");
            migrationBuilder.Sql("CREATE TRIGGER audit_events_no_truncate BEFORE TRUNCATE ON audit_events FOR EACH STATEMENT EXECUTE FUNCTION audit_events_protect();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_events_no_truncate ON audit_events;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_events_no_update_delete ON audit_events;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS audit_events_protect();");
            migrationBuilder.Sql("CREATE RULE audit_no_update AS ON UPDATE TO audit_events DO INSTEAD NOTHING;");
            migrationBuilder.Sql("CREATE RULE audit_no_delete AS ON DELETE TO audit_events DO INSTEAD NOTHING;");
            migrationBuilder.DropColumn(
                name: "hash",
                table: "audit_events");

            migrationBuilder.DropColumn(
                name: "prev_hash",
                table: "audit_events");
        }
    }
}
