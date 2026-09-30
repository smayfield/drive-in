using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DriveIn.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVehicleSizes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "vehicle_size",
                table: "tickets",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Standard");

            migrationBuilder.AddColumn<List<int>>(
                name: "large_spots",
                table: "screens",
                type: "integer[]",
                nullable: false,
                defaultValueSql: "'{}'");

            // Existing screens start with the back half of their rows (all of a one-row screen) taking large vehicles,
            // as Screen.BackHalfLarge does for new layouts. Spot keys are row * 100 + spot (Screen.SpotKey).
            migrationBuilder.Sql("""
                UPDATE screens SET large_spots = COALESCE((
                    SELECT array_agg(r * 100 + s ORDER BY r, s)
                    FROM generate_series(1, cardinality(row_spots)) AS r
                    CROSS JOIN LATERAL generate_series(1, row_spots[r]) AS s
                    WHERE r > cardinality(row_spots) / 2), '{}');
                """);

            // Moving tickets is new: existing Manager and Ticketing roles get it, as new theaters' defaults do.
            migrationBuilder.Sql("""
                INSERT INTO theater_role_permissions (role_id, permission)
                SELECT tr.id, 'tickets.move'
                FROM theater_roles tr
                WHERE tr.name IN ('Manager', 'Ticketing')
                  AND NOT EXISTS (SELECT 1 FROM theater_role_permissions x WHERE x.role_id = tr.id AND x.permission = 'tickets.move');
                """);

            migrationBuilder.CreateTable(
                name: "ticket_moves",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ticket_id = table.Column<int>(type: "integer", nullable: false),
                    from_row = table.Column<int>(type: "integer", nullable: false),
                    from_spot = table.Column<int>(type: "integer", nullable: false),
                    from_label = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    from_vehicle_size = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    to_row = table.Column<int>(type: "integer", nullable: false),
                    to_spot = table.Column<int>(type: "integer", nullable: false),
                    to_label = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    to_vehicle_size = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    moved_by_id = table.Column<string>(type: "text", nullable: true),
                    moved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_moves", x => x.id);
                    table.ForeignKey(
                        name: "fk_ticket_moves_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_ticket_moves_users_moved_by_id",
                        column: x => x.moved_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ticket_moves_moved_by_id",
                table: "ticket_moves",
                column: "moved_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_ticket_moves_ticket_id",
                table: "ticket_moves",
                column: "ticket_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ticket_moves");

            migrationBuilder.DropColumn(
                name: "vehicle_size",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "large_spots",
                table: "screens");
        }
    }
}
