using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DriveIn.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGiftCards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "gift_card_amount",
                table: "tickets",
                type: "numeric(8,2)",
                precision: 8,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "gift_card_id",
                table: "tickets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gift_card_last4",
                table: "tickets",
                type: "character varying(4)",
                maxLength: 4,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "gift_cards_enabled",
                table: "theaters",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "gift_cards",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    theater_id = table.Column<int>(type: "integer", nullable: false),
                    code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    initial_amount = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    balance = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    purchased_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    purchaser_id = table.Column<string>(type: "text", nullable: true),
                    purchaser_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    recipient_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    recipient_email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    card_brand = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    card_last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    payment_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    is_test = table.Column<bool>(type: "boolean", nullable: false),
                    stamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gift_cards", x => x.id);
                    table.CheckConstraint("ck_gift_cards_balance", "balance >= 0 AND balance <= initial_amount");
                    table.ForeignKey(
                        name: "fk_gift_cards_theaters_theater_id",
                        column: x => x.theater_id,
                        principalTable: "theaters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_gift_cards_users_purchaser_id",
                        column: x => x.purchaser_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "gift_card_transactions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    gift_card_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    balance_after = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: false),
                    ticket_id = table.Column<int>(type: "integer", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gift_card_transactions", x => x.id);
                    table.ForeignKey(
                        name: "fk_gift_card_transactions_gift_cards_gift_card_id",
                        column: x => x.gift_card_id,
                        principalTable: "gift_cards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tickets_gift_card_id",
                table: "tickets",
                column: "gift_card_id");

            migrationBuilder.CreateIndex(
                name: "ix_gift_card_transactions_gift_card_id",
                table: "gift_card_transactions",
                column: "gift_card_id");

            migrationBuilder.CreateIndex(
                name: "ix_gift_cards_code",
                table: "gift_cards",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gift_cards_purchaser_id",
                table: "gift_cards",
                column: "purchaser_id");

            migrationBuilder.CreateIndex(
                name: "ix_gift_cards_theater_id",
                table: "gift_cards",
                column: "theater_id");

            migrationBuilder.AddForeignKey(
                name: "fk_tickets_gift_cards_gift_card_id",
                table: "tickets",
                column: "gift_card_id",
                principalTable: "gift_cards",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            // Existing theaters: their Manager roles get the gift card actions (new theaters get them from
            // DefaultTheaterRoles). Owners can grant them to other roles.
            migrationBuilder.Sql("""
                INSERT INTO theater_role_permissions (role_id, permission)
                SELECT tr.id, p.permission
                FROM theater_roles tr
                CROSS JOIN (VALUES ('giftcards.manage'), ('giftcards.view')) AS p(permission)
                WHERE tr.name = 'Manager'
                  AND NOT EXISTS (SELECT 1 FROM theater_role_permissions x WHERE x.role_id = tr.id AND x.permission = p.permission);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_tickets_gift_cards_gift_card_id",
                table: "tickets");

            migrationBuilder.DropTable(
                name: "gift_card_transactions");

            migrationBuilder.DropTable(
                name: "gift_cards");

            migrationBuilder.DropIndex(
                name: "ix_tickets_gift_card_id",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "gift_card_amount",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "gift_card_id",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "gift_card_last4",
                table: "tickets");

            migrationBuilder.DropColumn(
                name: "gift_cards_enabled",
                table: "theaters");
        }
    }
}
