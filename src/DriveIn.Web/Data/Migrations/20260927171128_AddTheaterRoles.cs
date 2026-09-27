using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DriveIn.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTheaterRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "theater_roles",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    theater_id = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_theater_roles", x => x.id);
                    table.ForeignKey(
                        name: "fk_theater_roles_theaters_theater_id",
                        column: x => x.theater_id,
                        principalTable: "theaters",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "employee_roles",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    role_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_employee_roles", x => new { x.user_id, x.role_id });
                    table.ForeignKey(
                        name: "fk_employee_roles_theater_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "theater_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_employee_roles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "theater_role_permissions",
                columns: table => new
                {
                    role_id = table.Column<int>(type: "integer", nullable: false),
                    permission = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_theater_role_permissions", x => new { x.role_id, x.permission });
                    table.ForeignKey(
                        name: "fk_theater_role_permissions_theater_roles_role_id",
                        column: x => x.role_id,
                        principalTable: "theater_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_employee_roles_role_id",
                table: "employee_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_theater_roles_theater_id_name",
                table: "theater_roles",
                columns: new[] { "theater_id", "name" },
                unique: true);

            // Give existing theaters the default roles that new theaters get (DefaultTheaterRoles), as
            // they were when this migration was written. Existing employees get no roles, per the new
            // rule that employees can do nothing until assigned one.
            migrationBuilder.Sql("""
                INSERT INTO theater_roles (theater_id, name, description, created_at)
                SELECT t.id, r.name, r.description, now()
                FROM theaters t
                CROSS JOIN (VALUES
                    ('Manager', 'Runs the theater day to day, including staff and roles.'),
                    ('Operations', 'Keeps the theater''s details and screens up to date.'),
                    ('Ticketing', 'Box office staff. Ticket-sales actions will be added here as ticketing features ship.'),
                    ('Concessions', 'Snack bar staff. Concessions actions will be added here as concessions features ship.')
                ) AS r(name, description);

                INSERT INTO theater_role_permissions (role_id, permission)
                SELECT tr.id, p.permission
                FROM theater_roles tr
                JOIN (VALUES
                    ('Manager', 'theater.edit'), ('Manager', 'screens.manage'), ('Manager', 'employees.view'),
                    ('Manager', 'employees.invite'), ('Manager', 'employees.manage'), ('Manager', 'roles.manage'),
                    ('Operations', 'theater.edit'), ('Operations', 'screens.manage')
                ) AS p(role, permission) ON p.role = tr.name;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "employee_roles");

            migrationBuilder.DropTable(
                name: "theater_role_permissions");

            migrationBuilder.DropTable(
                name: "theater_roles");
        }
    }
}
