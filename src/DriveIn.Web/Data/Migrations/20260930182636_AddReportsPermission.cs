using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DriveIn.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReportsPermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing theaters: their Manager roles get reports (new theaters get it from DefaultTheaterRoles). Owners
            // can grant it to other roles.
            migrationBuilder.Sql("""
                INSERT INTO theater_role_permissions (role_id, permission)
                SELECT tr.id, 'reports.view'
                FROM theater_roles tr
                WHERE tr.normalized_name = 'MANAGER'
                  AND NOT EXISTS (SELECT 1 FROM theater_role_permissions x WHERE x.role_id = tr.id AND x.permission = 'reports.view');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately leaves the grants: Down can't tell the rows Up added from ones owners granted since, and code
            // without this permission ignores keys it doesn't know (TheaterAccess), so they do nothing until re-applied.
        }
    }
}
