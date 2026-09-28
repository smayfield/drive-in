using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DriveIn.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class TicketPricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "price_schedule_id",
                table: "showtimes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "add_ons",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    theater_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_add_ons", x => x.id);
                    table.ForeignKey(
                        name: "fk_add_ons_theaters_theater_id",
                        column: x => x.theater_id,
                        principalTable: "theaters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "price_schedules",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    theater_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_schedules", x => x.id);
                    table.ForeignKey(
                        name: "fk_price_schedules_theaters_theater_id",
                        column: x => x.theater_id,
                        principalTable: "theaters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "price_options",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    schedule_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    price = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_options", x => x.id);
                    table.ForeignKey(
                        name: "fk_price_options_price_schedules_schedule_id",
                        column: x => x.schedule_id,
                        principalTable: "price_schedules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_showtimes_price_schedule_id",
                table: "showtimes",
                column: "price_schedule_id");

            migrationBuilder.CreateIndex(
                name: "ix_add_ons_theater_id",
                table: "add_ons",
                column: "theater_id");

            migrationBuilder.CreateIndex(
                name: "ix_price_options_schedule_id",
                table: "price_options",
                column: "schedule_id");

            migrationBuilder.CreateIndex(
                name: "ix_price_schedules_one_default_per_theater",
                table: "price_schedules",
                column: "theater_id",
                unique: true,
                filter: "is_default");

            migrationBuilder.CreateIndex(
                name: "ix_price_schedules_theater_id_normalized_name",
                table: "price_schedules",
                columns: new[] { "theater_id", "normalized_name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_showtimes_price_schedules_price_schedule_id",
                table: "showtimes",
                column: "price_schedule_id",
                principalTable: "price_schedules",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            // Every theater has a default price schedule (empty until the owner adds prices), as new theaters do.
            migrationBuilder.Sql("""
                INSERT INTO price_schedules (theater_id, name, normalized_name, is_default)
                SELECT t.id, 'Standard', 'STANDARD', true
                FROM theaters t
                WHERE NOT EXISTS (SELECT 1 FROM price_schedules p WHERE p.theater_id = t.id AND p.is_default);
                """);

            // Prices are money, so only roles that already hold every other action (e.g. the default Manager
            // role, which new theaters give everything) get pricing.manage; owners grant it to others as they choose.
            migrationBuilder.Sql("""
                INSERT INTO theater_role_permissions (role_id, permission)
                SELECT p.role_id, 'pricing.manage'
                FROM theater_role_permissions p
                WHERE p.permission IN ('theater.edit', 'screens.manage', 'schedule.manage', 'employees.view',
                                       'employees.invite', 'employees.manage', 'roles.manage')
                GROUP BY p.role_id
                HAVING count(DISTINCT p.permission) = 7
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM theater_role_permissions WHERE permission = 'pricing.manage';");

            migrationBuilder.DropForeignKey(
                name: "fk_showtimes_price_schedules_price_schedule_id",
                table: "showtimes");

            migrationBuilder.DropTable(
                name: "add_ons");

            migrationBuilder.DropTable(
                name: "price_options");

            migrationBuilder.DropTable(
                name: "price_schedules");

            migrationBuilder.DropIndex(
                name: "ix_showtimes_price_schedule_id",
                table: "showtimes");

            migrationBuilder.DropColumn(
                name: "price_schedule_id",
                table: "showtimes");
        }
    }
}
