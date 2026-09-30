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
    public const string ManageSchedule = "schedule.manage";
    public const string ManagePricing = "pricing.manage";
    public const string ViewEmployees = "employees.view";
    public const string InviteEmployees = "employees.invite";
    public const string ManageEmployees = "employees.manage";
    public const string ManageRoles = "roles.manage";
    public const string AdmitGuests = "tickets.admit";
    public const string SellAtGate = "tickets.sell";
    public const string MoveTickets = "tickets.move";
    public const string OfferFreeAdmission = "comps.offer";
    public const string ApproveFreeAdmission = "comps.approve";
    public const string ViewFreeAdmission = "comps.view";
    public const string ManageGiftCards = "giftcards.manage";
    public const string ViewGiftCards = "giftcards.view";
    public const string ViewReports = "reports.view";
    public const string ViewBilling = "billing.view";
    public const string ManageBilling = "billing.manage";

    public static readonly IReadOnlyList<TheaterPermission> All =
    [
        new(EditProfile, "Theater", "Edit theater profile", "Change the theater's name, address, contact details and description."),
        new(ManageScreens, "Theater", "Manage screens", "Add, rename, reorder and delete screens and lay out their parking spots."),
        new(ManageSchedule, "Theater", "Manage schedule", "Add and edit films, schedule showings (including double features) on the screens, set the default intermission, and choose each showing's price schedule."),
        new(ManagePricing, "Theater", "Manage pricing", "Set up price schedules and ticket prices, and the add-ons (fees and discounts) offered with tickets."),
        new(ViewEmployees, "Staff", "View employees", "See the employee list, each employee's roles, and pending invitations."),
        new(InviteEmployees, "Staff", "Invite employees", "Send, resend and revoke employee invitations."),
        new(ManageEmployees, "Staff", "Manage employees", "Send employees password resets and delete employee accounts."),
        new(ManageRoles, "Staff", "Manage roles", "Create, edit and delete roles and assign them to employees. Only actions you have yourself can be granted."),
        new(AdmitGuests, "Tickets", "Admit guests", "Check tickets at the gate (scan the QR code or enter the 4-character gate code) and let the car in."),
        new(SellAtGate, "Tickets", "Sell tickets at the gate", "Sell tickets to cars at the gate: choose a showing and spot, take payment and let the car in."),
        new(MoveTickets, "Tickets", "Move tickets", "Move a sold ticket to another available spot at the same showing, e.g. when the car is too large for its spot. Works before or after the car is checked in."),
        new(OfferFreeAdmission, "Tickets", "Offer free admission", "Reserve a spot at a showing, free of charge, for a guest such as a friend or family member (when the theater allows free admission). If the theater requires approval, this requests it instead."),
        new(ApproveFreeAdmission, "Tickets", "Approve free admission", "Approve or deny requests for free admission, and withdraw free tickets. Your own free admission never needs approval."),
        new(ViewFreeAdmission, "Tickets", "View free admission log", "See every free admission offered, requested, approved, denied or withdrawn, and who gave it."),
        new(ManageGiftCards, "Tickets", "Manage gift cards", "Turn gift card sales on or off for the theater. Redeeming a gift card at the gate needs \"Sell tickets at the gate\"."),
        new(ViewGiftCards, "Tickets", "View gift cards", "See the gift cards sold, who bought each, and what balance is left (codes are shown as their last four characters only)."),
        new(ViewReports, "Reports", "View reports", "See ticket sales, attendance and gift card reports (sold, redeemed and still owed) for any dates, and download them as CSV."),
        new(ViewBilling, "Billing", "View billing", "See the theater's plan, its invoices and receipts, and what's owed."),
        new(ManageBilling, "Billing", "Manage billing", "Change where invoices are emailed, and cancel the theater's subscription."),
    ];

    public static readonly IReadOnlySet<string> AllKeys = All.Select(p => p.Key).ToHashSet();

    public static bool IsKnown(string key) => AllKeys.Contains(key);

    // The owner's business with us rather than running the theater, so no default role gets these.
    public static readonly IReadOnlySet<string> Billing = new HashSet<string> { ViewBilling, ManageBilling };
}

// Roles created for every new theater. Owners can rename, change or delete them.
public static class DefaultTheaterRoles
{
    public static readonly IReadOnlyList<(string Name, string Description, string[] Permissions)> All =
    [
        ("Manager", "Runs the theater day to day, including staff and roles.",
            TheaterPermissions.All.Select(p => p.Key).Where(k => !TheaterPermissions.Billing.Contains(k)).ToArray()),
        ("Operations", "Keeps the theater's details, screens and schedule up to date.",
            [TheaterPermissions.EditProfile, TheaterPermissions.ManageScreens, TheaterPermissions.ManageSchedule]),
        ("Ticketing", "Gate and box office staff: sells tickets at the gate, checks tickets, admits guests and moves cars to other spots.",
            [TheaterPermissions.AdmitGuests, TheaterPermissions.SellAtGate, TheaterPermissions.MoveTickets]),
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
