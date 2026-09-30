using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriveIn.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class GateSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "short_code",
                table: "tickets",
                type: "character varying(4)",
                maxLength: 4,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "sold_at_gate",
                table: "tickets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "sold_by_id",
                table: "tickets",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_tickets_short_code",
                table: "tickets",
                column: "short_code");

            migrationBuilder.CreateIndex(
                name: "ix_tickets_sold_by_id",
                table: "tickets",
                column: "sold_by_id");

            migrationBuilder.AddForeignKey(
                name: "fk_tickets_users_sold_by_id",
                table: "tickets",
                column: "sold_by_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            // Tickets already sold get a gate code too, from ShortCodes.Alphabet as it is now (a migration is a
            // snapshot). The subquery refers to the row so each ticket gets its own code.
            migrationBuilder.Sql("""
                UPDATE tickets SET short_code = (
                    SELECT string_agg(substr(a.chars, 1 + floor(random() * length(a.chars))::int, 1), '' ORDER BY g)
                    FROM (SELECT 'ABCDEFGHJKMNPQRTUVWXYZ2346789'::text AS chars) a, generate_series(1, 4) g
                    WHERE tickets.id IS NOT NULL)
                WHERE status = 'Sold' AND short_code IS NULL;
                """);

            // As for new theaters: roles that hold every other action (e.g. the default Manager role) and the
            // Ticketing role can sell at the gate.
            migrationBuilder.Sql("""
                INSERT INTO theater_role_permissions (role_id, permission)
                SELECT p.role_id, 'tickets.sell'
                FROM theater_role_permissions p
                WHERE p.permission IN ('theater.edit', 'screens.manage', 'schedule.manage', 'pricing.manage', 'employees.view',
                                       'employees.invite', 'employees.manage', 'roles.manage', 'tickets.admit')
                GROUP BY p.role_id
                HAVING count(DISTINCT p.permission) = 9
                ON CONFLICT DO NOTHING;
                """);
            migrationBuilder.Sql("""
                INSERT INTO theater_role_permissions (role_id, permission)
                SELECT r.id, 'tickets.sell' FROM theater_roles r WHERE r.normalized_name = 'TICKETING'
                ON CONFLICT DO NOTHING;
                """);
            migrationBuilder.Sql("""
                UPDATE theater_roles SET description = 'Gate and box office staff: sells tickets at the gate, checks tickets and admits guests.'
                WHERE normalized_name = 'TICKETING'
                  AND description = 'Gate and box office staff: checks tickets and admits guests.';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM theater_role_permissions WHERE permission = 'tickets.sell';");

            migrationBuilder.DropForeignKey(
                name: "fk_tickets_users_sold_by_id",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "ix_tickets_short_code",
                table: "tickets");

            migrationBuilder.DropIndex(
                name: "ix_tickets_sold_by_id",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "short_code",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "sold_at_gate",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "sold_by_id",
                table: "tickets");
        }
    }
}
