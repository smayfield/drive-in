using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DriveIn.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFreeAdmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "comp_reason",
                table: "tickets",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "guest_name",
                table: "tickets",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_comp",
                table: "tickets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "free_admission_enabled",
                table: "theaters",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "free_admission_max_per_employee_per_showing",
                table: "theaters",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "free_admission_max_per_showing",
                table: "theaters",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "free_admission_requires_approval",
                table: "theaters",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "free_admission_requires_reason",
                table: "theaters",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "comp_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    theater_id = table.Column<int>(type: "integer", nullable: false),
                    ticket_id = table.Column<int>(type: "integer", nullable: true),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: true),
                    actor_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    offered_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    showing_title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    showing_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    spot_label = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    guest_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_test = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comp_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_comp_events_theaters_theater_id",
                        column: x => x.theater_id,
                        principalTable: "theaters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_comp_events_theater_id_at",
                table: "comp_events",
                columns: new[] { "theater_id", "at" });

            // Existing theaters: their Manager roles get the new free admission actions (new theaters get them from
            // DefaultTheaterRoles). Owners can grant them to other roles.
            migrationBuilder.Sql("""
                INSERT INTO theater_role_permissions (role_id, permission)
                SELECT tr.id, p.permission
                FROM theater_roles tr
                CROSS JOIN (VALUES ('comps.offer'), ('comps.approve'), ('comps.view')) AS p(permission)
                WHERE tr.name = 'Manager'
                  AND NOT EXISTS (SELECT 1 FROM theater_role_permissions x WHERE x.role_id = tr.id AND x.permission = p.permission);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "comp_events");

            migrationBuilder.DropColumn(
                name: "comp_reason",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "guest_name",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "is_comp",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "free_admission_enabled",
                table: "theaters");

            migrationBuilder.DropColumn(
                name: "free_admission_max_per_employee_per_showing",
                table: "theaters");

            migrationBuilder.DropColumn(
                name: "free_admission_max_per_showing",
                table: "theaters");

            migrationBuilder.DropColumn(
                name: "free_admission_requires_approval",
                table: "theaters");

            migrationBuilder.DropColumn(
                name: "free_admission_requires_reason",
                table: "theaters");
        }
    }
}
