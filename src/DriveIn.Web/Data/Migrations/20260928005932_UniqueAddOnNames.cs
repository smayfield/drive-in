using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriveIn.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class UniqueAddOnNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_add_ons_theater_id",
                table: "add_ons");

            migrationBuilder.AddColumn<string>(
                name: "normalized_name",
                table: "add_ons",
                type: "character varying(60)",
                maxLength: 60,
                nullable: false,
                defaultValue: "");

            // Existing names were already unique ignoring case (checked by PricingService).
            migrationBuilder.Sql("UPDATE add_ons SET normalized_name = upper(trim(name));");

            migrationBuilder.CreateIndex(
                name: "ix_add_ons_theater_id_normalized_name",
                table: "add_ons",
                columns: new[] { "theater_id", "normalized_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_add_ons_theater_id_normalized_name",
                table: "add_ons");

            migrationBuilder.DropColumn(
                name: "normalized_name",
                table: "add_ons");

            migrationBuilder.CreateIndex(
                name: "ix_add_ons_theater_id",
                table: "add_ons",
                column: "theater_id");
        }
    }
}
