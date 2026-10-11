using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Rc7AccountMerge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_merge_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    survivor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    closed_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    privileged = table.Column<bool>(type: "boolean", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    effective_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancel_token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_merge_requests", x => x.id);
                    table.CheckConstraint("chk_account_merge_requests_two_accounts", "survivor_user_id <> closed_user_id");
                    table.ForeignKey(
                        name: "fk_account_merge_requests_asp_net_users_closed_user_id",
                        column: x => x.closed_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_account_merge_requests_asp_net_users_survivor_user_id",
                        column: x => x.survivor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "idx_account_merge_requests_closed",
                table: "account_merge_requests",
                column: "closed_user_id");

            migrationBuilder.CreateIndex(
                name: "idx_account_merge_requests_due",
                table: "account_merge_requests",
                column: "effective_at",
                filter: "cancelled_at IS NULL AND applied_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "idx_account_merge_requests_survivor",
                table: "account_merge_requests",
                column: "survivor_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_account_merge_requests_cancel_token",
                table: "account_merge_requests",
                column: "cancel_token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_merge_requests");
        }
    }
}
