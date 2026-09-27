using System.ComponentModel.DataAnnotations;

namespace DriveIn.Web.Data;

// An owner-defined role within one theater. Its permissions come from Authorization.TheaterPermissions.
public class TheaterRole
{
    public int Id { get; set; }

    public int TheaterId { get; set; }
    public Theater? Theater { get; set; }

    [Required, MaxLength(60)]
    public string Name
    {
        get;
        set
        {
            field = value;
            NormalizedName = Normalize(value);
        }
    } = "";

    // Upper-cased Name; unique per theater, so role names are unique ignoring case (enforced by the DB).
    [MaxLength(60)]
    public string NormalizedName { get; private set; } = "";

    public static string Normalize(string name) => name.Trim().ToUpperInvariant();

    [MaxLength(300)]
    public string? Description { get; set; }

    public List<TheaterRolePermission> Permissions { get; set; } = [];
    public List<EmployeeRole> Members { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class TheaterRolePermission
{
    public int RoleId { get; set; }
    public TheaterRole? Role { get; set; }

    // A TheaterPermissions key, e.g. "screens.manage".
    public string Permission { get; set; } = "";
}

// Assigns an employee to a role of their theater. An employee's rights are the union of their roles.
public class EmployeeRole
{
    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }

    public int RoleId { get; set; }
    public TheaterRole? Role { get; set; }
}
