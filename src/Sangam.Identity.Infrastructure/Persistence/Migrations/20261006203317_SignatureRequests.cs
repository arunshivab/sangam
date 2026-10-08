using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SignatureRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "signature_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    record_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    record_hash = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: false),
                    meaning = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    display_text = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    signer_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    return_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acr = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    amr = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    token = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_signature_requests", x => x.id);
                    table.CheckConstraint("chk_signature_requests_status", "status IN ('pending','signed','declined')");
                    table.ForeignKey(
                        name: "fk_signature_requests_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_signature_requests_users_decided_by_user_id",
                        column: x => x.decided_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "idx_signature_requests_app_created",
                table: "signature_requests",
                columns: new[] { "app_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_signature_requests_decided_by_user_id",
                table: "signature_requests",
                column: "decided_by_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "signature_requests");
        }
    }
}
