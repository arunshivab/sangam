using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Attributes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_claim_mappings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    claim_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attribute_key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_claim_mappings", x => x.id);
                    table.CheckConstraint("chk_app_claim_mappings_source", "source IN ('attribute','roles','permissions','org_names')");
                    table.ForeignKey(
                        name: "fk_app_claim_mappings_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_attribute_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    org_id = table.Column<Guid>(type: "uuid", nullable: true),
                    key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    choices = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    editable_by = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_attribute_definitions", x => x.id);
                    table.CheckConstraint("chk_user_attribute_definitions_editable", "editable_by IN ('admin','person')");
                    table.CheckConstraint("chk_user_attribute_definitions_type", "type IN ('text','number','date','boolean','choice')");
                    table.ForeignKey(
                        name: "fk_user_attribute_definitions_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_attribute_definitions_organisations_org_id",
                        column: x => x.org_id,
                        principalTable: "organisations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_attribute_values",
                columns: table => new
                {
                    definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_attribute_values", x => new { x.definition_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_user_attribute_values_user_attribute_definitions_definition",
                        column: x => x.definition_id,
                        principalTable: "user_attribute_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_attribute_values_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_app_claim_mappings_name",
                table: "app_claim_mappings",
                columns: new[] { "app_id", "claim_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_attribute_definitions_org_id",
                table: "user_attribute_definitions",
                column: "org_id");

            migrationBuilder.CreateIndex(
                name: "ux_user_attribute_definitions_live",
                table: "user_attribute_definitions",
                columns: new[] { "app_id", "org_id", "key" },
                unique: true,
                filter: "retired_at IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "idx_user_attribute_values_user",
                table: "user_attribute_values",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_claim_mappings");

            migrationBuilder.DropTable(
                name: "user_attribute_values");

            migrationBuilder.DropTable(
                name: "user_attribute_definitions");
        }
    }
}
