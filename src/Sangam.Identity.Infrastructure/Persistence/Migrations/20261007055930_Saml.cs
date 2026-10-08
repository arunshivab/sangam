using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Saml : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "saml_requests",
                columns: table => new
                {
                    handle = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    service_provider_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    acs_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    relay_state = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    required_level = table.Column<int>(type: "integer", nullable: false),
                    force_authn = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saml_requests", x => x.handle);
                });

            migrationBuilder.CreateTable(
                name: "saml_service_providers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    acs_urls = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    slo_url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    signing_certificate = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    encryption_certificate = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    name_id_format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attributes = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    require_signed_requests = table.Column<bool>(type: "boolean", nullable: false),
                    allow_idp_initiated = table.Column<bool>(type: "boolean", nullable: false),
                    default_relay_state = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saml_service_providers", x => x.id);
                    table.ForeignKey(
                        name: "fk_saml_service_providers_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "idx_saml_requests_expires",
                table: "saml_requests",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ux_saml_requests_sp_request",
                table: "saml_requests",
                columns: new[] { "service_provider_id", "request_id" },
                unique: true,
                filter: "request_id <> ''");

            migrationBuilder.CreateIndex(
                name: "ux_saml_service_providers_app_id",
                table: "saml_service_providers",
                column: "app_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_saml_service_providers_entity_id",
                table: "saml_service_providers",
                column: "entity_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "saml_requests");

            migrationBuilder.DropTable(
                name: "saml_service_providers");
        }
    }
}
