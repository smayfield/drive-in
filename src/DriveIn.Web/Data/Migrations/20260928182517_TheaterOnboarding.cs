using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriveIn.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class TheaterOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_test",
                table: "tickets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "go_live_requested_at",
                table: "theaters",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "live_since",
                table: "theaters",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mode",
                table: "theaters",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Live");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "terms_accepted_at",
                table: "theaters",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "terms_version",
                table: "theaters",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            // Every existing theater was set up by an admin and is already public: it has been live since it was created.
            migrationBuilder.Sql("UPDATE theaters SET live_since = created_at WHERE mode = 'Live' AND live_since IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_test",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "go_live_requested_at",
                table: "theaters");

            migrationBuilder.DropColumn(
                name: "live_since",
                table: "theaters");

            migrationBuilder.DropColumn(
                name: "mode",
                table: "theaters");

            migrationBuilder.DropColumn(
                name: "terms_accepted_at",
                table: "theaters");

            migrationBuilder.DropColumn(
                name: "terms_version",
                table: "theaters");
        }
    }
}
