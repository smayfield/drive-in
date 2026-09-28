using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DriveIn.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class TicketSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "season_closes_on",
                table: "theaters",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "season_opens_on",
                table: "theaters",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tickets",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    showtime_id = table.Column<int>(type: "integer", nullable: false),
                    row = table.Column<int>(type: "integer", nullable: false),
                    spot = table.Column<int>(type: "integer", nullable: false),
                    spot_label = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    held_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    stamp = table.Column<Guid>(type: "uuid", nullable: false),
                    sold_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    option_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    option_price = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    total = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    card_brand = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    card_last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    payment_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    admitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tickets", x => x.id);
                    table.ForeignKey(
                        name: "fk_tickets_showtimes_showtime_id",
                        column: x => x.showtime_id,
                        principalTable: "showtimes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_tickets_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ticket_add_ons",
                columns: table => new
                {
                    ticket_id = table.Column<int>(type: "integer", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    effect = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ticket_add_ons", x => new { x.ticket_id, x.position });
                    table.ForeignKey(
                        name: "fk_ticket_add_ons_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_code",
                table: "tickets",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tickets_showtime_id_row_spot",
                table: "tickets",
                columns: new[] { "showtime_id", "row", "spot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tickets_status_held_until",
                table: "tickets",
                columns: new[] { "status", "held_until" });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_user_id",
                table: "tickets",
                column: "user_id");

            // Existing theaters get what new ones start with: roles that hold every other action (e.g. the default
            // Manager role) and the Ticketing role (created empty for this) can admit guests at the gate.
            migrationBuilder.Sql("""
                INSERT INTO theater_role_permissions (role_id, permission)
                SELECT p.role_id, 'tickets.admit'
                FROM theater_role_permissions p
                WHERE p.permission IN ('theater.edit', 'screens.manage', 'schedule.manage', 'pricing.manage', 'employees.view',
                                       'employees.invite', 'employees.manage', 'roles.manage')
                GROUP BY p.role_id
                HAVING count(DISTINCT p.permission) = 8
                ON CONFLICT DO NOTHING;
                """);
            migrationBuilder.Sql("""
                INSERT INTO theater_role_permissions (role_id, permission)
                SELECT r.id, 'tickets.admit' FROM theater_roles r WHERE r.normalized_name = 'TICKETING'
                ON CONFLICT DO NOTHING;
                """);
            migrationBuilder.Sql("""
                UPDATE theater_roles SET description = 'Gate and box office staff: checks tickets and admits guests.'
                WHERE normalized_name = 'TICKETING'
                  AND description = 'Box office staff. Ticket-sales actions will be added here as ticketing features ship.';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM theater_role_permissions WHERE permission = 'tickets.admit';");

            migrationBuilder.DropTable(
                name: "ticket_add_ons");

            migrationBuilder.DropTable(
                name: "tickets");

            migrationBuilder.DropColumn(
                name: "season_closes_on",
                table: "theaters");

            migrationBuilder.DropColumn(
                name: "season_opens_on",
                table: "theaters");
        }
    }
}
