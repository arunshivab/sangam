using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GrievanceLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "grievances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    complainant_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    complainant_contact = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    acknowledge_by = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolve_by = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolution = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_grievances", x => x.id);
                    table.CheckConstraint("chk_grievances_status", "status IN ('received','acknowledged','resolved','declined')");
                });

            migrationBuilder.CreateTable(
                name: "grievance_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    grievance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    operator_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_grievance_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_grievance_entries_grievances_grievance_id",
                        column: x => x.grievance_id,
                        principalTable: "grievances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "idx_grievance_entries_grievance",
                table: "grievance_entries",
                columns: new[] { "grievance_id", "at" });

            migrationBuilder.CreateIndex(
                name: "idx_grievances_status_due",
                table: "grievances",
                columns: new[] { "status", "resolve_by" });

            migrationBuilder.CreateIndex(
                name: "ux_grievances_reference",
                table: "grievances",
                column: "reference",
                unique: true);

            // D-D: a grievance's history is append-only, like the audit log; a grievance is never deleted.
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION grievance_history_protect() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION '% is append-only: % is not allowed', TG_TABLE_NAME, TG_OP USING ERRCODE = 'insufficient_privilege';
END
$$;");
            migrationBuilder.Sql("CREATE TRIGGER grievance_entries_append_only BEFORE UPDATE OR DELETE ON grievance_entries FOR EACH ROW EXECUTE FUNCTION grievance_history_protect();");
            migrationBuilder.Sql("CREATE TRIGGER grievances_never_deleted BEFORE DELETE ON grievances FOR EACH ROW EXECUTE FUNCTION grievance_history_protect();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS grievances_never_deleted ON grievances;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS grievance_entries_append_only ON grievance_entries;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS grievance_history_protect();");

            migrationBuilder.DropTable(
                name: "grievance_entries");

            migrationBuilder.DropTable(
                name: "grievances");
        }
    }
}
