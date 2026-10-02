using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriveIn.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class TheaterPayoutAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "payout_account_id",
                table: "theaters",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payout_status",
                table: "theaters",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                // Existing theaters have no payout account yet (stored by name; "" wouldn't read back as an enum).
                defaultValue: "None");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "payout_status_checked_at",
                table: "theaters",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "payout_account_id",
                table: "theaters");

            migrationBuilder.DropColumn(
                name: "payout_status",
                table: "theaters");

            migrationBuilder.DropColumn(
                name: "payout_status_checked_at",
                table: "theaters");
        }
    }
}
