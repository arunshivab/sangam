using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Imagiqa.Web.Records.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "patient_mrn");

            migrationBuilder.CreateTable(
                name: "patients",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organisation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mrn = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "nextval('patient_mrn')"),
                    given_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    family_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    date_of_birth = table.Column<DateOnly>(type: "date", nullable: false),
                    sex = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    mobile = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    registered_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    registered_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_patients", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    written_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    written_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    written_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notes", x => x.id);
                    table.ForeignKey(
                        name: "fk_notes_patients_patient_id",
                        column: x => x.patient_id,
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "vitals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    recorded_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_by_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    pulse = table.Column<int>(type: "integer", nullable: true),
                    systolic = table.Column<int>(type: "integer", nullable: true),
                    diastolic = table.Column<int>(type: "integer", nullable: true),
                    temperature_c = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: true),
                    sp_o2 = table.Column<int>(type: "integer", nullable: true),
                    respiratory_rate = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vitals", x => x.id);
                    table.ForeignKey(
                        name: "fk_vitals_patients_patient_id",
                        column: x => x.patient_id,
                        principalTable: "patients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notes_patient_id_written_at",
                table: "notes",
                columns: new[] { "patient_id", "written_at" });

            migrationBuilder.CreateIndex(
                name: "ix_patients_mrn",
                table: "patients",
                column: "mrn",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_patients_organisation_id_registered_at",
                table: "patients",
                columns: new[] { "organisation_id", "registered_at" });

            migrationBuilder.CreateIndex(
                name: "ix_vitals_patient_id_recorded_at",
                table: "vitals",
                columns: new[] { "patient_id", "recorded_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notes");

            migrationBuilder.DropTable(
                name: "vitals");

            migrationBuilder.DropTable(
                name: "patients");

            migrationBuilder.DropSequence(
                name: "patient_mrn");
        }
    }
}
