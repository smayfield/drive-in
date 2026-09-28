using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DriveIn.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScreenLayoutsAndSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "label_scheme",
                table: "screens",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "LetterNumber");

            migrationBuilder.AddColumn<List<int>>(
                name: "row_spots",
                table: "screens",
                type: "integer[]",
                nullable: false,
                defaultValueSql: "'{}'");

            // Capacity is now the number of spots in the layout. Keep each screen's old car capacity as
            // rows of 20 spots (wider rows for big lots) so owners can adjust it rather than start over.
            migrationBuilder.Sql("""
                UPDATE screens s SET row_spots = ARRAY(
                    SELECT LEAST(w.width, s.car_capacity - (g - 1) * w.width)
                    FROM (SELECT LEAST(99, GREATEST(20, CEIL(s.car_capacity / 40.0)::int)) AS width) w,
                         generate_series(1, CEIL(s.car_capacity / w.width::numeric)::int) g
                    ORDER BY g)
                WHERE s.car_capacity > 0;
                """);

            migrationBuilder.DropColumn(
                name: "car_capacity",
                table: "screens");

            // Every theater has at least one screen.
            migrationBuilder.Sql("""
                INSERT INTO screens (theater_id, name, sort_order)
                SELECT t.id, 'Screen 1', 0
                FROM theaters t
                WHERE NOT EXISTS (SELECT 1 FROM screens s WHERE s.theater_id = t.id);
                """);

            // Scheduling used to be part of running the screens, so roles that manage screens
            // (including the default Manager and Operations roles) also get the new schedule permission.
            migrationBuilder.Sql("""
                INSERT INTO theater_role_permissions (role_id, permission)
                SELECT p.role_id, 'schedule.manage'
                FROM theater_role_permissions p
                WHERE p.permission = 'screens.manage'
                ON CONFLICT DO NOTHING;
                """);

            migrationBuilder.CreateTable(
                name: "films",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    theater_id = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    rating = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    runtime_minutes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_films", x => x.id);
                    table.ForeignKey(
                        name: "fk_films_theaters_theater_id",
                        column: x => x.theater_id,
                        principalTable: "theaters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "showtimes",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    screen_id = table.Column<int>(type: "integer", nullable: false),
                    film_id = table.Column<int>(type: "integer", nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_showtimes", x => x.id);
                    table.ForeignKey(
                        name: "fk_showtimes_films_film_id",
                        column: x => x.film_id,
                        principalTable: "films",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_showtimes_screens_screen_id",
                        column: x => x.screen_id,
                        principalTable: "screens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_films_theater_id",
                table: "films",
                column: "theater_id");

            migrationBuilder.CreateIndex(
                name: "ix_showtimes_film_id",
                table: "showtimes",
                column: "film_id");

            migrationBuilder.CreateIndex(
                name: "ix_showtimes_screen_id_starts_at",
                table: "showtimes",
                columns: new[] { "screen_id", "starts_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "showtimes");

            migrationBuilder.DropTable(
                name: "films");

            migrationBuilder.Sql("DELETE FROM theater_role_permissions WHERE permission = 'schedule.manage';");

            migrationBuilder.AddColumn<int>(
                name: "car_capacity",
                table: "screens",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("UPDATE screens SET car_capacity = COALESCE((SELECT SUM(n) FROM unnest(row_spots) n), 0);");

            migrationBuilder.DropColumn(
                name: "label_scheme",
                table: "screens");

            migrationBuilder.DropColumn(
                name: "row_spots",
                table: "screens");
        }
    }
}
