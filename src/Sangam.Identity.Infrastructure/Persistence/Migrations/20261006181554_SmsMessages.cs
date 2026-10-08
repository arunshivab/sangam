using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SmsMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sms_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    to_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ip_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    template = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    provider_message_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    error = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sms_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_sms_messages_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "idx_sms_messages_created",
                table: "sms_messages",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "idx_sms_messages_ip_created",
                table: "sms_messages",
                columns: new[] { "ip_hash", "created_at" });

            migrationBuilder.CreateIndex(
                name: "idx_sms_messages_provider_message",
                table: "sms_messages",
                columns: new[] { "provider", "provider_message_id" });

            migrationBuilder.CreateIndex(
                name: "idx_sms_messages_to_created",
                table: "sms_messages",
                columns: new[] { "to_hash", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_sms_messages_user_id",
                table: "sms_messages",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sms_messages");
        }
    }
}
