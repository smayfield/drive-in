using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DriveIn.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMessaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "email_notifications",
                table: "users",
                type: "boolean",
                nullable: false,
                // Existing accounts get notification emails, like new ones (ApplicationUser.EmailNotifications).
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "conversations",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    theater_id = table.Column<int>(type: "integer", nullable: true),
                    about_theater_id = table.Column<int>(type: "integer", nullable: true),
                    customer_id = table.Column<string>(type: "text", nullable: true),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversations", x => x.id);
                    table.ForeignKey(
                        name: "fk_conversations_theaters_about_theater_id",
                        column: x => x.about_theater_id,
                        principalTable: "theaters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_conversations_theaters_theater_id",
                        column: x => x.theater_id,
                        principalTable: "theaters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_conversations_users_customer_id",
                        column: x => x.customer_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "conversation_reads",
                columns: table => new
                {
                    conversation_id = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_conversation_reads", x => new { x.conversation_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_conversation_reads_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_conversation_reads_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    conversation_id = table.Column<int>(type: "integer", nullable: false),
                    sender_id = table.Column<string>(type: "text", nullable: true),
                    side = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_messages_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_messages_users_sender_id",
                        column: x => x.sender_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    link = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    conversation_id = table.Column<int>(type: "integer", nullable: true),
                    count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    emailed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_notifications_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_notifications_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_conversation_reads_user_id",
                table: "conversation_reads",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_conversations_about_theater_id",
                table: "conversations",
                column: "about_theater_id");

            migrationBuilder.CreateIndex(
                name: "ix_conversations_customer_id_last_message_at",
                table: "conversations",
                columns: new[] { "customer_id", "last_message_at" });

            migrationBuilder.CreateIndex(
                name: "ix_conversations_kind_last_message_at",
                table: "conversations",
                columns: new[] { "kind", "last_message_at" });

            migrationBuilder.CreateIndex(
                name: "ix_conversations_theater_id_last_message_at",
                table: "conversations",
                columns: new[] { "theater_id", "last_message_at" });

            migrationBuilder.CreateIndex(
                name: "ix_messages_conversation_id_sent_at",
                table: "messages",
                columns: new[] { "conversation_id", "sent_at" });

            migrationBuilder.CreateIndex(
                name: "ix_messages_sender_id",
                table: "messages",
                column: "sender_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_conversation_id",
                table: "notifications",
                column: "conversation_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_read_at_emailed_at_updated_at",
                table: "notifications",
                columns: new[] { "read_at", "emailed_at", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_user_id_read_at",
                table: "notifications",
                columns: new[] { "user_id", "read_at" });

            // Existing theaters: their Manager roles get the new messages permissions (new theaters get them from
            // DefaultTheaterRoles). Owners can grant them to other roles.
            migrationBuilder.Sql("""
                INSERT INTO theater_role_permissions (role_id, permission)
                SELECT tr.id, p.permission
                FROM theater_roles tr
                CROSS JOIN (VALUES ('messages.view'), ('messages.reply')) AS p(permission)
                WHERE tr.normalized_name = 'MANAGER'
                  AND NOT EXISTS (SELECT 1 FROM theater_role_permissions x WHERE x.role_id = tr.id AND x.permission = p.permission);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Leaves the Manager grants from Up (see AddReportsPermission): code without these permissions ignores
            // keys it doesn't know.
            migrationBuilder.DropTable(
                name: "conversation_reads");

            migrationBuilder.DropTable(
                name: "messages");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "conversations");

            migrationBuilder.DropColumn(
                name: "email_notifications",
                table: "users");
        }
    }
}
