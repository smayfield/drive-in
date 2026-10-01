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

internal static class UploadedImages
{
    // The MIME type if the bytes start like a JPEG, GIF or PNG, else null. Logos and posters.
    public static string? Sniff(ReadOnlySpan<byte> d) => d switch
    {
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'7' or (byte)'9', (byte)'a', ..] => "image/gif",
        _ => null,
    };

    // Content images may also be WebP (a RIFF container whose form type is WEBP).
    public static string? SniffContent(ReadOnlySpan<byte> d) =>
        Sniff(d) ?? (d is [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..]
            ? "image/webp" : null);

    // Width and height from the image's header, without decoding it; null if they can't be read.
    public static (int Width, int Height)? Dimensions(ReadOnlySpan<byte> d, string contentType)
    {
        try
        {
            return contentType switch
            {
                "image/png" when d.Length >= 24 => (BigEndian(d[16..20]), BigEndian(d[20..24])),
                "image/gif" when d.Length >= 10 => (d[6] | d[7] << 8, d[8] | d[9] << 8),
                "image/jpeg" => Jpeg(d),
                "image/webp" => WebP(d),
                _ => null,
            };
        }
        catch (IndexOutOfRangeException)
        {
            return null; // truncated header
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static int BigEndian(ReadOnlySpan<byte> b) => b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3];

    // Walks the JPEG markers to the first start-of-frame (SOF0-SOF15, except DHT, JPG and DAC).
    private static (int, int)? Jpeg(ReadOnlySpan<byte> d)
    {
        var i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF)
                return null;
            var marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; }
            var length = d[i + 2] << 8 | d[i + 3];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return (d[i + 7] << 8 | d[i + 8], d[i + 5] << 8 | d[i + 6]);
            i += 2 + length;
        }
        return null;
    }

    private static (int, int)? WebP(ReadOnlySpan<byte> d)
    {
        if (d.Length < 30)
            return null;
        var chunk = System.Text.Encoding.ASCII.GetString(d[12..16]);
        return chunk switch
        {
            // Lossy: 14-bit sizes after the frame tag and start code.
            "VP8 " => (d[26] | (d[27] & 0x3F) << 8, d[28] | (d[29] & 0x3F) << 8),
            // Lossless: 14-bit width-1 and height-1 packed after the signature byte.
            "VP8L" => (1 + (d[21] | (d[22] & 0x3F) << 8), 1 + ((d[22] >> 6) | d[23] << 2 | (d[24] & 0x0F) << 10)),
            // Extended: 24-bit canvas width-1 and height-1.
            "VP8X" => (1 + (d[24] | d[25] << 8 | d[26] << 16), 1 + (d[27] | d[28] << 8 | d[29] << 16)),
            _ => null,
        };
    }
}
