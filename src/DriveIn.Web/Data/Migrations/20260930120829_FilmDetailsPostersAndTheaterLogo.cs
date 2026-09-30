using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriveIn.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class FilmDetailsPostersAndTheaterLogo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "logo_updated_at",
                table: "theaters",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cast",
                table: "films",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "directors",
                table: "films",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "genres",
                table: "films",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "overview",
                table: "films",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "poster_updated_at",
                table: "films",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "release_year",
                table: "films",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "film_posters",
                columns: table => new
                {
                    film_id = table.Column<int>(type: "integer", nullable: false),
                    content_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_film_posters", x => x.film_id);
                    table.ForeignKey(
                        name: "fk_film_posters_films_film_id",
                        column: x => x.film_id,
                        principalTable: "films",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "theater_logos",
                columns: table => new
                {
                    theater_id = table.Column<int>(type: "integer", nullable: false),
                    content_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_theater_logos", x => x.theater_id);
                    table.ForeignKey(
                        name: "fk_theater_logos_theaters_theater_id",
                        column: x => x.theater_id,
                        principalTable: "theaters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "film_posters");

            migrationBuilder.DropTable(
                name: "theater_logos");

            migrationBuilder.DropColumn(
                name: "logo_updated_at",
                table: "theaters");

            migrationBuilder.DropColumn(
                name: "cast",
                table: "films");

            migrationBuilder.DropColumn(
                name: "directors",
                table: "films");

            migrationBuilder.DropColumn(
                name: "genres",
                table: "films");

            migrationBuilder.DropColumn(
                name: "overview",
                table: "films");

            migrationBuilder.DropColumn(
                name: "poster_updated_at",
                table: "films");

            migrationBuilder.DropColumn(
                name: "release_year",
                table: "films");
        }
    }
}
