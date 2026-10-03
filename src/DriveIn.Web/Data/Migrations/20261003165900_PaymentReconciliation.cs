using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DriveIn.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class PaymentReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "payment_key",
                table: "tickets",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "payment_started_at",
                table: "tickets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "gift_card_purchases",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    theater_id = table.Column<int>(type: "integer", nullable: false),
                    purchaser_id = table.Column<string>(type: "text", nullable: true),
                    purchaser_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    purchaser_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    recipient_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    recipient_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_test = table.Column<bool>(type: "boolean", nullable: false),
                    gift_card_id = table.Column<int>(type: "integer", nullable: true),
                    stamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gift_card_purchases", x => x.id);
                    table.ForeignKey(
                        name: "fk_gift_card_purchases_gift_cards_gift_card_id",
                        column: x => x.gift_card_id,
                        principalTable: "gift_cards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_gift_card_purchases_theaters_theater_id",
                        column: x => x.theater_id,
                        principalTable: "theaters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_gift_card_purchases_users_purchaser_id",
                        column: x => x.purchaser_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_payment_key",
                table: "tickets",
                column: "payment_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gift_card_purchases_gift_card_id",
                table: "gift_card_purchases",
                column: "gift_card_id");

            migrationBuilder.CreateIndex(
                name: "ix_gift_card_purchases_purchaser_id",
                table: "gift_card_purchases",
                column: "purchaser_id");

            migrationBuilder.CreateIndex(
                name: "ix_gift_card_purchases_status_started_at",
                table: "gift_card_purchases",
                columns: new[] { "status", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_gift_card_purchases_theater_id",
                table: "gift_card_purchases",
                column: "theater_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gift_card_purchases");

            migrationBuilder.DropIndex(
                name: "ix_tickets_payment_key",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "payment_key",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "payment_started_at",
                table: "tickets");
        }
    }
}
