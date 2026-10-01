using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DriveIn.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTheaterContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "theater_images",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    theater_id = table.Column<int>(type: "integer", nullable: false),
                    file_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    alt_text = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    content_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    byte_size = table.Column<int>(type: "integer", nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    uploaded_by_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_theater_images", x => x.id);
                    table.ForeignKey(
                        name: "fk_theater_images_theaters_theater_id",
                        column: x => x.theater_id,
                        principalTable: "theaters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_theater_images_users_uploaded_by_id",
                        column: x => x.uploaded_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "theater_pages",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    theater_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    body_html = table.Column<string>(type: "text", nullable: false),
                    cover_image_id = table.Column<int>(type: "integer", nullable: true),
                    cover_alt = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    publish_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    unpublish_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    show_in_menu = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_pinned = table.Column<bool>(type: "boolean", nullable: false),
                    event_starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_theater_pages", x => x.id);
                    table.ForeignKey(
                        name: "fk_theater_pages_theater_images_cover_image_id",
                        column: x => x.cover_image_id,
                        principalTable: "theater_images",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_theater_pages_theaters_theater_id",
                        column: x => x.theater_id,
                        principalTable: "theaters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_theater_pages_users_updated_by_id",
                        column: x => x.updated_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_theater_images_theater_id_uploaded_at",
                table: "theater_images",
                columns: new[] { "theater_id", "uploaded_at" });

            migrationBuilder.CreateIndex(
                name: "ix_theater_images_uploaded_by_id",
                table: "theater_images",
                column: "uploaded_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_theater_pages_cover_image_id",
                table: "theater_pages",
                column: "cover_image_id");

            migrationBuilder.CreateIndex(
                name: "ix_theater_pages_theater_id_kind_publish_at",
                table: "theater_pages",
                columns: new[] { "theater_id", "kind", "publish_at" });

            migrationBuilder.CreateIndex(
                name: "ix_theater_pages_theater_id_kind_slug",
                table: "theater_pages",
                columns: new[] { "theater_id", "kind", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_theater_pages_updated_by_id",
                table: "theater_pages",
                column: "updated_by_id");

            // Existing theaters: their Manager and Operations roles get content.manage (new theaters get it from
            // DefaultTheaterRoles). Owners can grant it to other roles.
            migrationBuilder.Sql("""
                INSERT INTO theater_role_permissions (role_id, permission)
                SELECT tr.id, 'content.manage'
                FROM theater_roles tr
                WHERE tr.normalized_name IN ('MANAGER', 'OPERATIONS')
                  AND NOT EXISTS (SELECT 1 FROM theater_role_permissions x WHERE x.role_id = tr.id AND x.permission = 'content.manage');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Leaves the grants from Up (see AddReportsPermission): code without this permission ignores keys it doesn't
            // know.
            migrationBuilder.DropTable(
                name: "theater_pages");

            migrationBuilder.DropTable(
                name: "theater_images");
        }
    }
}
