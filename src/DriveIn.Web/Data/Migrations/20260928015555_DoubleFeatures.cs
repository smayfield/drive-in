using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriveIn.Web.Data.Migrations
{
    // Hand-written: the scaffold treated showtimes.film_id -> intermission_minutes as a rename, which would have
    // turned film ids into intermission lengths. Instead each showtime's film moves to showtime_features.
    /// <inheritdoc />
    public partial class DoubleFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "default_intermission_minutes",
                table: "theaters",
                type: "integer",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.CreateTable(
                name: "showtime_features",
                columns: table => new
                {
                    showtime_id = table.Column<int>(type: "integer", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    film_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_showtime_features", x => new { x.showtime_id, x.position });
                    table.ForeignKey(
                        name: "fk_showtime_features_films_film_id",
                        column: x => x.film_id,
                        principalTable: "films",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_showtime_features_showtimes_showtime_id",
                        column: x => x.showtime_id,
                        principalTable: "showtimes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_showtime_features_film_id",
                table: "showtime_features",
                column: "film_id");

            // Every existing showtime is a single feature.
            migrationBuilder.Sql("""
                INSERT INTO showtime_features (showtime_id, position, film_id)
                SELECT id, 1, film_id FROM showtimes;
                """);

            // The theater default (15) at the time; it only matters if the showing becomes a double feature.
            migrationBuilder.AddColumn<int>(
                name: "intermission_minutes",
                table: "showtimes",
                type: "integer",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ends_at",
                table: "showtimes",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.Sql("""
                UPDATE showtimes s
                SET ends_at = s.starts_at + make_interval(mins => f.runtime_minutes)
                FROM films f
                WHERE f.id = s.film_id;

                ALTER TABLE showtimes ALTER COLUMN ends_at DROP DEFAULT;
                """);

            migrationBuilder.DropForeignKey(
                name: "fk_showtimes_films_film_id",
                table: "showtimes");

            migrationBuilder.DropIndex(
                name: "ix_showtimes_film_id",
                table: "showtimes");

            migrationBuilder.DropColumn(
                name: "film_id",
                table: "showtimes");
        }

        // Double features go back to their first film (the others are dropped from the showing).
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "film_id",
                table: "showtimes",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE showtimes s
                SET film_id = f.film_id
                FROM showtime_features f
                WHERE f.showtime_id = s.id AND f.position = 1;

                DELETE FROM showtimes WHERE film_id IS NULL;
                """);

            migrationBuilder.AlterColumn<int>(
                name: "film_id",
                table: "showtimes",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_showtimes_film_id",
                table: "showtimes",
                column: "film_id");

            migrationBuilder.AddForeignKey(
                name: "fk_showtimes_films_film_id",
                table: "showtimes",
                column: "film_id",
                principalTable: "films",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.DropTable(
                name: "showtime_features");

            migrationBuilder.DropColumn(
                name: "ends_at",
                table: "showtimes");

            migrationBuilder.DropColumn(
                name: "intermission_minutes",
                table: "showtimes");

            migrationBuilder.DropColumn(
                name: "default_intermission_minutes",
                table: "theaters");
        }
    }
}
