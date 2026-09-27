using DriveIn.Web.Data;

namespace DriveIn.Web.Authorization;

public sealed record TheaterPermission(string Key, string Group, string Name, string Description);

// The actions an owner can grant to theater roles. Every theater operation an employee can perform
// must be guarded by one of these (checked in the service layer). When adding functionality, add a
// permission here and require it in the service; the Roles UI and default roles pick it up from All.
// Keys are stored in the database, so never rename or reuse one.
public static class TheaterPermissions
{
    public const string EditProfile = "theater.edit";
    public const string ManageScreens = "screens.manage";
    public const string ViewEmployees = "employees.view";
    public const string InviteEmployees = "employees.invite";
    public const string ManageEmployees = "employees.manage";
    public const string ManageRoles = "roles.manage";

    public static readonly IReadOnlyList<TheaterPermission> All =
    [
        new(EditProfile, "Theater", "Edit theater profile", "Change the theater's name, address, contact details and description."),
        new(ManageScreens, "Theater", "Manage screens", "Add, rename, reorder and delete screens and set their car capacity."),
        new(ViewEmployees, "Staff", "View employees", "See the employee list, each employee's roles, and pending invitations."),
        new(InviteEmployees, "Staff", "Invite employees", "Send, resend and revoke employee invitations."),
        new(ManageEmployees, "Staff", "Manage employees", "Send employees password resets and delete employee accounts."),
        new(ManageRoles, "Staff", "Manage roles", "Create, edit and delete roles and assign them to employees. Only actions you have yourself can be granted."),
    ];

    public static readonly IReadOnlySet<string> AllKeys = All.Select(p => p.Key).ToHashSet();

    public static bool IsKnown(string key) => AllKeys.Contains(key);
}

// Roles created for every new theater. Owners can rename, change or delete them.
public static class DefaultTheaterRoles
{
    public static readonly IReadOnlyList<(string Name, string Description, string[] Permissions)> All =
    [
        ("Manager", "Runs the theater day to day, including staff and roles.",
            TheaterPermissions.All.Select(p => p.Key).ToArray()),
        ("Operations", "Keeps the theater's details and screens up to date.",
            [TheaterPermissions.EditProfile, TheaterPermissions.ManageScreens]),
        ("Ticketing", "Box office staff. Ticket-sales actions will be added here as ticketing features ship.",
            []),
        ("Concessions", "Snack bar staff. Concessions actions will be added here as concessions features ship.",
            []),
    ];

    public static List<TheaterRole> CreateFor(Theater theater, DateTimeOffset createdAt) =>
        All.Select(r => new TheaterRole
        {
            Theater = theater,
            CreatedAt = createdAt,
            Name = r.Name,
            Description = r.Description,
            Permissions = r.Permissions.Select(p => new TheaterRolePermission { Permission = p }).ToList(),
        }).ToList();
}
