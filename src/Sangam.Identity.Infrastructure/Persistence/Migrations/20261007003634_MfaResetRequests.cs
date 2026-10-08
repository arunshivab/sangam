using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MfaResetRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mfa_reset_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    verification_method = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    privileged = table.Column<bool>(type: "boolean", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    effective_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancel_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    urgent_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    urgent_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    notice_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mfa_reset_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_mfa_reset_requests_asp_net_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_mfa_reset_requests_due",
                table: "mfa_reset_requests",
                column: "effective_at",
                filter: "cancelled_at IS NULL AND applied_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "idx_mfa_reset_requests_user",
                table: "mfa_reset_requests",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_mfa_reset_requests_cancel_token",
                table: "mfa_reset_requests",
                column: "cancel_token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mfa_reset_requests");
        }
    }
}
