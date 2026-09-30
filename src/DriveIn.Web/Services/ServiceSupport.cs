using System.Security.Claims;
using System.Text;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

// The caller isn't allowed to do this. Pages show AccessDenied.
public sealed class AccessDeniedException(string message = "You don't have access to do that.") : Exception(message);

// A business rule rejected the request; the message is safe to show the user.
public sealed class AppValidationException(string message) : Exception(message);

public sealed class NotFoundException(string message = "Not found.") : Exception(message);

internal static class Guard
{
    public static async Task RequireAsync(this IAuthorizationService auth, ClaimsPrincipal user, Theater theater,
        string permission)
    {
        var result = await auth.AuthorizeAsync(user, theater, new TheaterPermissionRequirement(permission));
        if (!result.Succeeded)
            throw new AccessDeniedException();
    }

    // Admin, owner, or one of the theater's employees (regardless of roles).
    public static void RequireMember(ClaimsPrincipal user, Theater theater)
    {
        if (!TheaterAccess.IsMember(user, theater))
            throw new AccessDeniedException();
    }

    // Anti-escalation: a non-owner can only grant, revoke or touch permissions they hold themselves.
    public static void RequireWithinAuthority(IReadOnlySet<string> actorPermissions, IEnumerable<string> permissions, string message)
    {
        if (!permissions.Where(TheaterPermissions.IsKnown).All(actorPermissions.Contains))
            throw new AppValidationException(message);
    }

    public static void RequireAdmin(ClaimsPrincipal user)
    {
        if (!user.IsAdmin())
            throw new AccessDeniedException();
    }

    public static string RequireUserId(ClaimsPrincipal user) =>
        user.GetUserId() ?? throw new AccessDeniedException("You must be signed in.");

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}

internal static class DbErrors
{
    // A unique index rejected the save (Postgres; the InMemory provider used by tests has no unique indexes).
    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };
}

internal static class AccountLinks
{
    // Same format as the Identity UI's ForgotPassword page, so Account/ResetPassword accepts it.
    public static string PasswordReset(string baseUri, string token)
    {
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        return QueryHelpers.AddQueryString(Combine(baseUri, "Account/ResetPassword"), "code", code);
    }

    public static string Invite(string baseUri, string token) => Combine(baseUri, $"invite/{Uri.EscapeDataString(token)}");

    private static string Combine(string baseUri, string path) => baseUri.TrimEnd('/') + "/" + path;
}

internal static class TicketRecords
{
    // Tickets that are sold, or being paid for, are sales records: their showings can't be removed. Holds on
    // showings that are going away are dropped.
    public static async Task RemoveHoldsOrThrowAsync(ApplicationDbContext db, IReadOnlyCollection<int> showtimeIds, string soldMessage)
    {
        if (showtimeIds.Count == 0)
            return;
        var tickets = await db.Tickets.Where(t => showtimeIds.Contains(t.ShowtimeId)).ToListAsync();
        if (tickets.Any(t => t.Status != TicketStatus.Held))
            throw new AppValidationException(soldMessage);
        db.Tickets.RemoveRange(tickets);
    }

    // Tickets that hold or own a spot at these showings now (expired holds don't count).
    public static IQueryable<Ticket> Active(ApplicationDbContext db, DateTimeOffset now) =>
        db.Tickets.Where(t => t.Status != TicketStatus.Held || t.HeldUntil > now);
}
