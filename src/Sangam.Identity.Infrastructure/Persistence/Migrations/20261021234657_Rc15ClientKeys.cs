using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Rc15ClientKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "oaep_sha256",
                table: "saml_service_providers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reminder_sent_at",
                table: "client_secrets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "issued_secret_reminder_at",
                table: "apps",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "client_assertion_uses",
                columns: table => new
                {
                    client_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    token_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_assertion_uses", x => new { x.client_id, x.token_id });
                });

            migrationBuilder.CreateTable(
                name: "client_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    jwk = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_client_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_client_keys_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_client_assertion_uses_expires",
                table: "client_assertion_uses",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "idx_client_keys_app_live",
                table: "client_keys",
                columns: new[] { "app_id", "revoked_at" });

            migrationBuilder.CreateIndex(
                name: "ux_client_keys_live_key",
                table: "client_keys",
                columns: new[] { "app_id", "key_id" },
                unique: true,
                filter: "revoked_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "client_assertion_uses");

            migrationBuilder.DropTable(
                name: "client_keys");

            migrationBuilder.DropColumn(
                name: "oaep_sha256",
                table: "saml_service_providers");

            migrationBuilder.DropColumn(
                name: "reminder_sent_at",
                table: "client_secrets");

            migrationBuilder.DropColumn(
                name: "issued_secret_reminder_at",
                table: "apps");
        }
    }
}
