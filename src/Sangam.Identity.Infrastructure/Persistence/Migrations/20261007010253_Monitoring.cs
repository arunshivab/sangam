using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sangam.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Monitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "metric_points",
                columns: table => new
                {
                    minute = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    host = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    tag = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    count = table.Column<long>(type: "bigint", nullable: false),
                    sum = table.Column<double>(type: "double precision", nullable: false),
                    max = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_metric_points", x => new { x.minute, x.host, x.name, x.tag });
                });

            migrationBuilder.CreateTable(
                name: "monitoring_alerts",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_monitoring_alerts", x => x.key);
                });

            migrationBuilder.CreateIndex(
                name: "idx_metric_points_name_minute",
                table: "metric_points",
                columns: new[] { "name", "minute" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "metric_points");

            migrationBuilder.DropTable(
                name: "monitoring_alerts");
        }
    }
}
