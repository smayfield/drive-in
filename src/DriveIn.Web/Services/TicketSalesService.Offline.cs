using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

// What the offline gate page (/manage/{id}/gate/offline) keeps on the device: tonight's showings and their sold tickets.
// Ticket codes are sent only as SHA-256 hashes, so a lost phone or tablet doesn't hold usable ticket links; the 4-character
// gate codes are sent as they are (they're on the guest's receipt to be read out, and only admit at this theater tonight).
// No buyer or guest names or emails are sent.
public sealed record OfflineGateList(string Theater, string TimeZone, DateTimeOffset GeneratedAt,
    IReadOnlyList<OfflineGateShowing> Showings, IReadOnlyList<OfflineGateTicket> Tickets);

// Times are UTC; the labels are in the theater's time zone, as the online gate shows them.
public sealed record OfflineGateShowing(int Id, string Title, string Screen, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    DateTimeOffset GatesOpenAt, string StartsLabel, string DateLabel, string GatesOpenLabel);

// Kind: online, gate or comp.
public sealed record OfflineGateTicket(int Id, int ShowingId, string CodeHash, string? ShortCode, string Spot, bool Large,
    string Kind, bool Test, DateTimeOffset? AdmittedAt);

// A check-in made on the device. AdmissionId is random, made by the device for each check-in, and makes a retried sync
// harmless. AdmittedAt is the device's clock; Spot is the spot the device showed, to tell staff if the car was moved since.
public sealed record OfflineAdmission(int TicketId, Guid AdmissionId, DateTimeOffset AdmittedAt, string? Spot);

public static class OfflineSyncOutcomes
{
    // Applied now.
    public const string Admitted = "admitted";
    // This same check-in was applied by an earlier sync (a retry after a lost response).
    public const string AlreadySynced = "already_synced";
    // Conflicts: the car is already in, but the ticket was used by another check-in, is gone (a withdrawn free ticket),
    // isn't this theater's, or wasn't valid at the time.
    public const string AlreadyUsed = "already_used";
    public const string NotFound = "not_found";
    public const string NotValid = "not_valid";

    public static bool IsConflict(string outcome) => outcome is AlreadyUsed or NotFound or NotValid;
}

// CurrentSpot is set when the ticket's spot isn't the one the device showed (it was moved since).
public sealed record OfflineSyncResult(Guid AdmissionId, int TicketId, string Outcome, string? Message, string? CurrentSpot);

public sealed partial class TicketSalesService
{
    // The most check-ins one sync may carry; the page sends its queue in batches of this size.
    public const int MaxOfflineSyncBatch = 200;

    // Tonight's admit list: the showings the gate can admit to today (as for gate sales: today's in the theater's time
    // zone that haven't ended, plus any still running), with every sold ticket for them, used or not.
    public async Task<OfflineGateList> GetOfflineGateListAsync(ClaimsPrincipal user, int theaterId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await FindTheaterAsync(db, theaterId);
        await auth.RequireAsync(user, theater, TheaterPermissions.AdmitGuests);
        var now = time.GetUtcNow();
        var today = DateOnly.FromDateTime(TheaterTime.ToLocal(theater, now));
        var showtimes = (await ScheduleService.WithFeatures(db.Showtimes.AsNoTracking()).Include(s => s.Screen)
                .Where(s => s.Screen!.TheaterId == theaterId && s.EndsAt > now && s.StartsAt < now.AddDays(2))
                .ToListAsync())
            .Where(s => s.StartsAt <= now || DateOnly.FromDateTime(TheaterTime.ToLocal(theater, s.StartsAt)) == today)
            .OrderBy(s => s.StartsAt).ThenBy(s => s.Screen!.SortOrder)
            .ToList();
        var ids = showtimes.Select(s => s.Id).ToList();
        var tickets = await db.Tickets.AsNoTracking()
            .Where(t => ids.Contains(t.ShowtimeId) && t.Status == TicketStatus.Sold && t.Code != null)
            .OrderBy(t => t.ShowtimeId).ThenBy(t => t.Row).ThenBy(t => t.Spot)
            .ToListAsync();

        var showings = showtimes.Select(s =>
        {
            var starts = TheaterTime.ToLocal(theater, s.StartsAt);
            var opens = s.StartsAt.AddHours(-AdmitOpensHoursBefore);
            return new OfflineGateShowing(s.Id, ScheduleService.ToView(theater, s).Title, s.Screen!.Name, s.StartsAt, s.EndsAt, opens,
                $"{starts:ddd, MMM d · h:mm tt}", $"{starts:ddd, MMM d}", $"{TheaterTime.ToLocal(theater, opens):h:mm tt}");
        }).ToList();
        return new OfflineGateList(theater.Name, TheaterTime.ZoneOf(theater).Id, now, showings, tickets.Select(t => new OfflineGateTicket(
            t.Id, t.ShowtimeId, HashTicketCode(t.Code!), t.ShortCode, t.SpotLabel, t.VehicleSize == VehicleSize.Large,
            t.IsComp ? "comp" : t.SoldAtGate ? "gate" : "online", t.IsTest, t.AdmittedAt)).ToList());
    }

    // Applies check-ins made while the device was offline, under the same rules as checking in online, judged at the
    // time the car came in (so a check-in synced after the showing ended still counts). Each is applied once: the ticket's
    // Stamp is set to the check-in's AdmissionId, so a retried sync recognizes its own check-in. Anything refused is a
    // conflict for staff to look at (the car is already in); nothing here throws for one bad entry.
    public async Task<List<OfflineSyncResult>> SyncOfflineAdmissionsAsync(ClaimsPrincipal user, int theaterId,
        IReadOnlyList<OfflineAdmission> admissions)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await FindTheaterAsync(db, theaterId);
        await auth.RequireAsync(user, theater, TheaterPermissions.AdmitGuests);
        if (admissions.Count > MaxOfflineSyncBatch)
            throw new AppValidationException($"Send at most {MaxOfflineSyncBatch} check-ins at a time.");
        if (admissions.Any(a => a.AdmissionId == Guid.Empty))
            throw new AppValidationException("Every check-in needs an id.");

        var now = time.GetUtcNow();
        var ticketIds = admissions.Select(a => a.TicketId).Distinct().ToList();
        var tickets = await db.Tickets.Include(t => t.Showtime!.Screen!.Theater)
            .Where(t => ticketIds.Contains(t.Id) && t.Status == TicketStatus.Sold && t.Showtime!.Screen!.TheaterId == theaterId)
            .ToDictionaryAsync(t => t.Id);

        var results = new List<OfflineSyncResult>();
        foreach (var admission in admissions.OrderBy(a => a.AdmittedAt))
        {
            var result = await ApplyOfflineAdmissionAsync(db, tickets.GetValueOrDefault(admission.TicketId), admission, now);
            results.Add(result);
            if (result.Outcome == OfflineSyncOutcomes.Admitted)
            {
                metrics.TicketAdmitted("offline");
                metrics.OfflineAdmissionSynced(conflict: false);
            }
            else if (OfflineSyncOutcomes.IsConflict(result.Outcome))
                metrics.OfflineAdmissionSynced(conflict: true);
        }
        return results;
    }

    private async Task<OfflineSyncResult> ApplyOfflineAdmissionAsync(ApplicationDbContext db, Ticket? ticket,
        OfflineAdmission admission, DateTimeOffset now)
    {
        OfflineSyncResult Result(string outcome, string? message) => new(admission.AdmissionId, admission.TicketId, outcome, message,
            ticket is not null && admission.Spot is not null && admission.Spot != ticket.SpotLabel ? ticket.SpotLabel : null);

        if (ticket is null)
            return Result(OfflineSyncOutcomes.NotFound,
                "This ticket is no longer valid here (a withdrawn free ticket, or not one of this theater's).");
        if (ticket.AdmittedAt is DateTimeOffset used)
            return ticket.Stamp == admission.AdmissionId
                ? Result(OfflineSyncOutcomes.AlreadySynced, null)
                : Result(OfflineSyncOutcomes.AlreadyUsed,
                    $"Already used: admitted {TheaterTime.ToLocal(ticket.Showtime!.Screen!.Theater!, used):ddd, MMM d h:mm tt} by another check-in.");

        // A device clock ahead of ours can't admit a car in the future.
        var at = admission.AdmittedAt > now ? now : admission.AdmittedAt;
        if (AdmitProblem(ticket, at) is string problem)
            return Result(OfflineSyncOutcomes.NotValid, $"When it was checked in: {problem}");

        ticket.AdmittedAt = at;
        // Doubles as the record of which check-in this was (see SyncOfflineAdmissionsAsync); it's still a new value, so it
        // still guards against a concurrent check-in.
        ticket.Stamp = admission.AdmissionId;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Checked in (or moved) by someone else just now: look again.
            await db.Entry(ticket).ReloadAsync();
            if (db.Entry(ticket).State == EntityState.Detached || ticket.Status != TicketStatus.Sold)
            {
                ticket = null;
                return Result(OfflineSyncOutcomes.NotFound, "This ticket is no longer valid here.");
            }
            return ticket.AdmittedAt is null
                ? await ApplyOfflineAdmissionAsync(db, ticket, admission, now)
                : Result(OfflineSyncOutcomes.AlreadyUsed, "Already used: another check-in got there first.");
        }
        return Result(OfflineSyncOutcomes.Admitted, null);
    }

    // How the offline page matches a scanned or typed ticket code without holding the code itself.
    public static string HashTicketCode(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}
