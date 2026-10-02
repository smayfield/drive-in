using System.Security.Claims;
using System.Security.Cryptography;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DriveIn.Web.Services;

// A showing as offered for sale. Local times are in the theater's time zone.
// Payment is how the checkout page takes a card for this theater (Stripe's fields, the test card form, or none).
public sealed record ShowingForSale(
    Theater Theater, Screen Screen, ShowtimeView Showing, PriceSchedule Prices, List<AddOn> AddOns, string? NotOnSaleReason,
    PaymentClient Payment)
{
    public bool OnSale => NotOnSaleReason is null;
}

public enum SpotState
{
    Available,
    Held,  // someone else is paying for it; it comes back if their hold runs out
    Sold,
    Mine,  // held by the viewer
}

public sealed record HoldView(int TicketId, int Row, int Spot, string SpotLabel, DateTimeOffset HeldUntil, VehicleSize VehicleSize);

// Spots that aren't listed are available.
public sealed record SpotAvailability(IReadOnlyDictionary<(int Row, int Spot), SpotState> Spots, HoldView? MyHold)
{
    public SpotState this[int row, int spot] => Spots.GetValueOrDefault((row, spot), SpotState.Available);
}

// PaymentMethodId: the token the buyer's browser made for their card (see PaymentClient); the card itself never
// reaches the server. GiftCardCode: a gift card to spend toward the total first; the card is charged only for what's left.
public sealed record PurchaseInput(int PriceOptionId, IReadOnlyList<int> AddOnIds, string? PaymentMethodId, string? GiftCardCode = null);

public sealed record PurchaseResult(string Code, bool ReceiptSent);

// A sold ticket with its showing (local times in the theater's time zone).
public sealed record TicketView(Ticket Ticket, Theater Theater, ShowtimeView Showing, DateTime SoldLocal, DateTime? AdmittedLocal)
{
    public string Code => Ticket.Code!;
}

// A ticket looked up by its code, as the viewer may see it: its buyer, or gate staff who can admit or move it.
// MoveProblem says why it can't be moved now (e.g. the showing has ended); an admitted ticket can still be moved.
public sealed record TicketLookup(TicketView View, bool IsBuyer, bool CanAdmit, string? AdmitProblem,
    bool CanMove = false, string? MoveProblem = null);

// Online ticket sales. Buyers are any signed-in user: they pick a showing and a spot on its screen, hold the spot for
// Ticket.HoldMinutes while they pay, and get an emailed receipt whose QR code is checked at the gate. Admitting
// guests requires AdmitGuests at the theater. Every change to a showing's spots is published on SpotEvents so
// open seat maps update live.
public sealed partial class TicketSalesService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IAuthorizationService auth,
    TheaterAccess access,
    IPaymentProcessor payments,
    DummyPaymentProcessor testPayments,
    IAppEmailSender email,
    SpotEvents events,
    TimeProvider time,
    DriveInMetrics metrics,
    IOptions<PaymentOptions> paymentOptions,
    ILogger<TicketSalesService> logger)
{
    // Gates open this long before the first film; a ticket admits until the showing ends.
    public const int AdmitOpensHoursBefore = 3;

    // --- Browsing ---

    // The showings of an active theater that haven't started yet, soonest first.
    // Signed in or not: the theater's page is public (CanBrowse still hides demo and inactive theaters).
    public async Task<(Theater Theater, List<ShowtimeView> Showings)> ListOnSaleAsync(ClaimsPrincipal user, string slug)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == slug && t.IsActive)
            ?? throw new NotFoundException("Theater not found.");
        if (!TheaterService.CanBrowse(user, theater))
            throw new NotFoundException("Theater not found.");
        var now = time.GetUtcNow();
        var showtimes = await ScheduleService.WithFeatures(db.Showtimes.AsNoTracking()).Include(s => s.Screen)
            .Where(s => s.Screen!.TheaterId == theater.Id && s.StartsAt > now)
            .ToListAsync();
        return (theater, showtimes
            .OrderBy(s => s.StartsAt).ThenBy(s => s.Screen!.SortOrder)
            .Select(s => ScheduleService.ToView(theater, s))
            .ToList());
    }

    public async Task<ShowingForSale> GetShowingAsync(ClaimsPrincipal user, int showtimeId)
    {
        Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var sale = await LoadShowingForSaleAsync(db, showtimeId, atGate: false);
        if (!TheaterService.CanBrowse(user, sale.Theater))
            throw new NotFoundException("Showing not found.");
        return sale;
    }

    private async Task<ShowingForSale> LoadShowingForSaleAsync(ApplicationDbContext db, int showtimeId, bool atGate)
    {
        var showtime = await ScheduleService.WithFeatures(db.Showtimes.AsNoTracking()).Include(s => s.Screen!.Theater)
            .FirstOrDefaultAsync(s => s.Id == showtimeId);
        if (showtime is null || !showtime.Screen!.Theater!.IsActive)
            throw new NotFoundException("Showing not found.");
        var theater = showtime.Screen.Theater;
        var prices = await LoadPricesAsync(db, showtime);
        var addOns = await db.AddOns.AsNoTracking().Where(a => a.TheaterId == theater.Id && a.IsActive)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Id).ToListAsync();
        return new ShowingForSale(theater, showtime.Screen, ScheduleService.ToView(theater, showtime), prices, addOns,
            NotOnSaleReason(showtime, prices, atGate), ProcessorFor(theater).Client);
    }

    public async Task<SpotAvailability> GetAvailabilityAsync(ClaimsPrincipal user, int showtimeId)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = time.GetUtcNow();
        var tickets = await db.Tickets.AsNoTracking()
            .Where(t => t.ShowtimeId == showtimeId && (t.Status != TicketStatus.Held || t.HeldUntil > now))
            .ToListAsync();
        var spots = new Dictionary<(int, int), SpotState>();
        HoldView? mine = null;
        foreach (var t in tickets)
        {
            var state = t.Status == TicketStatus.Sold ? SpotState.Sold
                : t.Status == TicketStatus.Held && t.UserId == userId ? SpotState.Mine : SpotState.Held;
            spots[(t.Row, t.Spot)] = state;
            if (t.Status == TicketStatus.Held && t.UserId == userId)
                mine = ToHold(t);
        }
        return new SpotAvailability(spots, mine);
    }

    // --- Buying ---

    // Holds a spot for the user while they pay; the first to hold a spot gets it. Holding another spot lets go of
    // any spot the user already holds, so one person can't tie up several at once. A large vehicle can only hold a
    // spot marked for large vehicles.
    public async Task<HoldView> HoldAsync(ClaimsPrincipal user, int showtimeId, int row, int spot, VehicleSize vehicle = VehicleSize.Standard)
    {
        var userId = Guard.RequireUserId(user);
        await using (var db = await dbFactory.CreateDbContextAsync())
            if (!TheaterService.CanBrowse(user, await TheaterOfShowtimeAsync(db, showtimeId)))
                throw new NotFoundException("Showing not found.");
        return await HoldAsync(userId, showtimeId, row, spot, vehicle, atGate: false);
    }

    private async Task<HoldView> HoldAsync(string userId, int showtimeId, int row, int spot, VehicleSize vehicle, bool atGate)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await TryHoldAsync(userId, showtimeId, row, spot, vehicle, atGate);
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                // An expired hold we were clearing was removed first (by the sweeper or another buyer): look again.
            }
        }
    }

    private async Task<HoldView> TryHoldAsync(string userId, int showtimeId, int row, int spot, VehicleSize vehicle, bool atGate)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var showtime = await db.Showtimes.AsNoTracking().Include(s => s.Screen!.Theater)
            .FirstOrDefaultAsync(s => s.Id == showtimeId);
        if (showtime is null || !showtime.Screen!.Theater!.IsActive)
            throw new NotFoundException("Showing not found.");
        if (NotOnSaleReason(showtime, await LoadPricesAsync(db, showtime), atGate) is string reason)
            throw new AppValidationException(reason);
        var label = CheckSpot(showtime.Screen, row, spot, vehicle);
        var now = time.GetUtcNow();

        var existing = await db.Tickets.FirstOrDefaultAsync(t => t.ShowtimeId == showtimeId && t.Row == row && t.Spot == spot);
        if (existing is { Status: TicketStatus.Held } && existing.UserId == userId && existing.HeldUntil > now)
        {
            // Already theirs; holding it again doesn't extend the hold, but does record a change of vehicle.
            if (existing.VehicleSize != vehicle)
            {
                existing.VehicleSize = vehicle;
                existing.Stamp = Guid.NewGuid();
                await db.SaveChangesAsync(); // DbUpdateConcurrencyException (it just expired): retried by HoldAsync
            }
            return ToHold(existing);
        }
        if (existing is not null && !(existing.Status == TicketStatus.Held && existing.HeldUntil <= now))
            throw Taken(label);

        // One spot at a time includes a checkout that's being charged (e.g. in another tab). Only within its hold
        // window, so a payment interrupted by a crash can't lock the buyer out for good.
        if (await db.Tickets.AnyAsync(t => t.UserId == userId && t.Status == TicketStatus.Paying && t.HeldUntil > now))
            throw new AppValidationException("You're paying for another spot right now. Finish that checkout first.");
        var released = await db.Tickets.Where(t => t.UserId == userId && t.Status == TicketStatus.Held).ToListAsync();
        var ticket = new Ticket
        {
            ShowtimeId = showtimeId, Row = row, Spot = spot, SpotLabel = label, VehicleSize = vehicle, UserId = userId,
            Status = TicketStatus.Held, HeldUntil = now.AddMinutes(Ticket.HoldMinutes), CreatedAt = now,
        };
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            // Deletes first, in their own save, so an expired hold on this spot is gone before the new one goes in.
            db.Tickets.RemoveRange(released);
            if (existing is not null && !released.Contains(existing))
                db.Tickets.Remove(existing);
            await db.SaveChangesAsync(); // DbUpdateConcurrencyException: retried by HoldAsync
            db.Tickets.Add(ticket);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                throw Taken(label); // someone else got there first
            }
            await tx.CommitAsync();
        }

        foreach (var id in released.Select(t => t.ShowtimeId).Append(showtimeId).Distinct())
            events.Publish(id);
        return ToHold(ticket);
    }

    // Lets go of the user's hold so the spot goes back on sale.
    public async Task ReleaseHoldAsync(ClaimsPrincipal user, int ticketId)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId && t.UserId == userId && t.Status == TicketStatus.Held);
        if (ticket is null)
            return;
        db.Tickets.Remove(ticket);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return; // already expired and released
        }
        events.Publish(ticket.ShowtimeId);
    }

    // Pays for a held spot. The hold must still be the user's and not have run out. On approval the spot is sold to
    // them and a receipt with the ticket's QR code and gate code is emailed. Sales are final.
    public async Task<PurchaseResult> PurchaseAsync(ClaimsPrincipal user, int ticketId, PurchaseInput input, string baseUri)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var (ticket, completedHere) = await SellHeldAsync(db, userId, ticketId, input.PriceOptionId, input.AddOnIds,
            input.PaymentMethodId, input.GiftCardCode, atGate: false);
        // If a processor webhook finished the sale first, it sent the receipt too.
        var sent = !completedHere || await TrySendReceiptAsync(await LoadViewAsync(db, ticket.Id), baseUri);
        return new PurchaseResult(ticket.Code!, sent);
    }

    // Charges for a spot the seller holds (the buyer online; the employee at the gate) and sells it. Online, the
    // buyer's card (its payment method token) is charged and the ticket is theirs. At the gate the charge is card-present (the processor's
    // terminal), the ticket has no buyer account, and the car is admitted as it's sold. A gift card (of the theater's)
    // pays first, and only the rest is charged to the card; if that charge fails the gift card is made whole again.
    //
    // Everything the sale needs is saved with the ticket going to Paying (price, add-ons, gate code, PaymentKey), so a
    // charge whose outcome isn't heard (the processor timed out, or the server died) can be finished or undone later
    // by CompletePaidTicketAsync / AbortTicketPaymentAsync, the same code this uses. Only a definite decline undoes the
    // sale here; an error leaves it Paying for PaymentReconcileService to settle with the processor.
    // Returns the sold ticket, and whether this call sold it (false if a webhook got there first).
    private async Task<(Ticket Ticket, bool CompletedHere)> SellHeldAsync(ApplicationDbContext db, string userId, int ticketId, int priceOptionId,
        IReadOnlyList<int> addOnIds, string? paymentMethodId, string? giftCardCode, bool atGate)
    {
        var ticket = await db.Tickets.Include(t => t.Showtime!.Screen!.Theater)
            .FirstOrDefaultAsync(t => t.Id == ticketId && t.UserId == userId);
        var now = time.GetUtcNow();
        if (ticket is null || ticket.Status != TicketStatus.Held || ticket.HeldUntil <= now)
            throw new AppValidationException("The hold on this spot ran out. Choose a spot again.");
        var showtime = ticket.Showtime!;
        var theater = showtime.Screen!.Theater!;
        var prices = await LoadPricesAsync(db, showtime);
        if (NotOnSaleReason(showtime, prices, atGate) is string reason)
            throw new AppValidationException(reason);

        var option = prices.Options.FirstOrDefault(o => o.Id == priceOptionId)
            ?? throw new AppValidationException("Choose a ticket type.");
        var ids = addOnIds.Distinct().ToList();
        var addOns = await db.AddOns.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.TheaterId == theater.Id && a.IsActive)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Id).ToListAsync();
        if (addOns.Count != ids.Count)
            throw new AppValidationException("One of the add-ons chosen is no longer offered. Check the choices and try again.");
        var quote = TicketQuote.For(option, addOns);
        var gift = quote.Total > 0 && !string.IsNullOrWhiteSpace(giftCardCode) ? await FindGiftCardAsync(db, theater.Id, giftCardCode, forUpdate: true) : null;
        var giftAmount = gift is null ? 0m : Math.Min(gift.Balance, quote.Total);
        var cardAmount = quote.Total - giftAmount;
        string? paymentMethod = null;
        string? buyerEmail = null;
        if (!atGate)
        {
            paymentMethod = cardAmount > 0 ? PaymentTokens.Require(paymentMethodId) : null;
            buyerEmail = (await db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync())?.Trim();
            if (string.IsNullOrEmpty(buyerEmail))
                throw new AppValidationException("Your account needs an email address to receive tickets.");
        }
        // Paying: the hold can no longer expire out from under the charge. The gift card's share comes off its balance in
        // the same save, so a balance can't be spent twice; a concurrent spend makes this fail rather than overdraw it.
        ticket.Status = TicketStatus.Paying;
        ticket.Stamp = Guid.NewGuid();
        ticket.PaymentKey = $"ticket-{ticket.Id}-{ticket.Stamp:N}";
        ticket.PaymentStartedAt = now;
        ticket.OptionName = option.Name;
        ticket.OptionPrice = option.Price;
        ticket.AddOns = quote.Lines.Select((l, i) => new TicketAddOn
        {
            Position = i + 1, Name = l.AddOn.Name, Kind = l.AddOn.Kind, Amount = l.AddOn.Amount, Effect = l.Effect,
        }).ToList();
        ticket.Total = quote.Total;
        ticket.IsTest = theater.IsDemo;
        ticket.ShortCode = await NewShortCodeAsync(db, theater.Id, now);
        ticket.Email = buyerEmail;
        if (atGate)
        {
            ticket.SoldAtGate = true;
            ticket.SoldById = userId;
        }
        if (gift is not null)
            SpendGiftCard(db, gift, ticket, giftAmount, now);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException ex) when (gift is not null && ex.Entries.Any(e => e.Entity is GiftCard))
        {
            throw new AppValidationException("The gift card's balance just changed (it may have been used elsewhere). Check it and try again.");
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new AppValidationException("The hold on this spot ran out. Choose a spot again.");
        }

        PaymentResult result;
        if (cardAmount == 0)
            result = new PaymentResult(true, null);
        else
        {
            var description = $"{theater.Name}: {ScheduleService.ToView(theater, await WithFeaturesAsync(db, showtime)).Title}, spot {ticket.SpotLabel}";
            try
            {
                result = await ProcessorFor(theater).ChargeAsync(TicketCharge(ticket, theater, cardAmount, description, paymentMethod));
            }
            catch (Exception ex)
            {
                // We don't know whether the card was charged, so the ticket stays Paying (its spot off sale) until the
                // processor says, rather than risk charging twice or selling the spot twice. Only the processor's own
                // failures count as payment errors (they page someone).
                metrics.Payment("ticket", "error", theater.IsDemo);
                logger.LogError(ex, "Couldn't confirm the charge for ticket {TicketId} ({PaymentKey}); left for reconciliation",
                    ticket.Id, ticket.PaymentKey);
                throw new AppValidationException(atGate
                    ? "The payment couldn't be confirmed. Don't take the card again: if it went through, the ticket is sold within a few minutes; if not, the spot goes back on sale."
                    : "We couldn't confirm your payment just now. You won't be charged twice: if it went through, your ticket appears under My tickets within a few minutes and is emailed to you; if not, the spot goes back on sale.");
            }
            metrics.Payment("ticket", result.Approved ? "approved" : "declined", theater.IsDemo);
        }
        if (!result.Approved)
        {
            await AbortTicketPaymentAsync(ticket.Id, ticket.PaymentKey);
            throw new AppValidationException(atGate
                ? $"The card was declined: {result.DeclineReason ?? "declined"}. Try another card."
                : $"Your payment wasn't approved: {result.DeclineReason ?? "declined"}. Check your card details or try another card.");
        }

        var (sold, completedHere) = await CompletePaidTicketAsync(ticket.Id, ticket.PaymentKey, result);
        return (sold ?? throw new InvalidOperationException($"Ticket {ticket.Id} was paid for but is no longer Paying."), completedHere);
    }

    // --- Tickets ---

    public async Task<List<TicketView>> ListMyTicketsAsync(ClaimsPrincipal user)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var tickets = await TicketsWithShowings(db)
            .Where(t => t.UserId == userId && t.Status == TicketStatus.Sold)
            .ToListAsync();
        return tickets.Select(ToView).OrderBy(v => v.Ticket.Showtime!.StartsAt).ToList();
    }

    // The buyer sees their ticket; staff who can admit guests at its theater see it with the admit option.
    public async Task<TicketLookup> GetByCodeAsync(ClaimsPrincipal user, string code)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ticket = await TicketsWithShowings(db).FirstOrDefaultAsync(t => t.Code == code && t.Status == TicketStatus.Sold)
            ?? throw new NotFoundException("Ticket not found.");
        var isBuyer = ticket.UserId == userId;
        var canAdmit = (await auth.AuthorizeAsync(user, ticket.Showtime!.Screen!.Theater!,
            new TheaterPermissionRequirement(TheaterPermissions.AdmitGuests))).Succeeded;
        if (!isBuyer && !canAdmit)
            throw new AccessDeniedException();
        return new TicketLookup(ToView(ticket), isBuyer, canAdmit, AdmitProblem(ticket));
    }

    // Lets the car in: the ticket must be for a showing whose gates are open today, and not used already.
    public async Task AdmitAsync(ClaimsPrincipal user, string code)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var ticket = await db.Tickets.Include(t => t.Showtime!.Screen!.Theater)
            .FirstOrDefaultAsync(t => t.Code == code && t.Status == TicketStatus.Sold)
            ?? throw new NotFoundException("Ticket not found.");
        await auth.RequireAsync(user, ticket.Showtime!.Screen!.Theater!, TheaterPermissions.AdmitGuests);
        if (AdmitProblem(ticket) is string problem)
            throw new AppValidationException(problem);
        ticket.AdmittedAt = time.GetUtcNow();
        ticket.Stamp = Guid.NewGuid();
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new AppValidationException("This ticket was just used.");
        }
        metrics.TicketAdmitted("scan");
    }

    public async Task<bool> ResendReceiptAsync(ClaimsPrincipal user, string code, string baseUri)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ticket = await TicketsWithShowings(db)
            .FirstOrDefaultAsync(t => t.Code == code && t.Status == TicketStatus.Sold && t.UserId == userId)
            ?? throw new NotFoundException("Ticket not found.");
        return await TrySendReceiptAsync(ToView(ticket), baseUri);
    }

    // --- Housekeeping ---

    // Deletes holds that ran out and tells open seat maps. Run periodically by HoldExpiryService; also safe to run
    // alongside buyers, since everything else treats an expired hold as available.
    // Paying tickets are deliberately left alone, even stale ones: one left by a crash mid-charge may have been paid
    // for, and resale would sell that spot twice. It blocks one spot at one showing; when a real processor is added,
    // reconcile these by asking it whether the charge went through (see TicketStatus.Paying).
    public async Task<int> ReleaseExpiredHoldsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = time.GetUtcNow();
        var expired = await db.Tickets.Where(t => t.Status == TicketStatus.Held && t.HeldUntil <= now).ToListAsync(ct);
        if (expired.Count == 0)
            return 0;
        db.Tickets.RemoveRange(expired);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return 0; // a buyer took one of these spots meanwhile; the next pass gets the rest
        }
        metrics.HoldsExpired(expired.Count);
        foreach (var id in expired.Select(t => t.ShowtimeId).Distinct())
            events.Publish(id);
        return expired.Count;
    }

    // --- Helpers ---

    // The charge for a ticket in Paying. Its key (Ticket.PaymentKey) is the ticket and its Paying stamp, so retrying
    // this same charge can't charge twice, while a later checkout of the same ticket (after a decline put it back to
    // Held) is a new charge.
    private PaymentRequest TicketCharge(Ticket ticket, Theater theater, decimal amount, string description, string? paymentMethod) =>
        Charge(theater, amount, description, paymentMethod, ticket.PaymentKey!, new Dictionary<string, string>
        {
            ["kind"] = "ticket", ["ticket_id"] = ticket.Id.ToString(), ["theater_id"] = theater.Id.ToString(),
            ["showtime_id"] = ticket.ShowtimeId.ToString(),
        });

    // A charge of the theater's money: paid to its payout account where the processor uses one, less the platform's fee.
    private PaymentRequest Charge(Theater theater, decimal amount, string description, string? paymentMethod, string key,
        Dictionary<string, string> metadata)
    {
        var cents = PaymentRequest.ToCents(amount);
        var payout = ProcessorFor(theater).RequiresPayoutAccount ? theater.PayoutAccountId : null;
        return new PaymentRequest(cents, paymentOptions.Value.Currency, description, paymentMethod, key, metadata, payout,
            payout is null ? 0 : paymentOptions.Value.ApplicationFeeCents(cents));
    }

    // Demo theaters always sell through the dummy processor (test tickets, no money), even where real payments
    // aren't set up, so prospective owners can try the whole flow.
    private IPaymentProcessor ProcessorFor(Theater theater) => theater.IsDemo ? testPayments : payments;

    // A live theater whose processor pays theaters directly (Stripe) can't take a card until its payout account is
    // enabled, or its money would have nowhere to go.
    private bool NeedsPayoutAccount(Theater theater) =>
        ProcessorFor(theater).RequiresPayoutAccount && theater.PayoutStatus != PayoutStatus.Enabled;

    // Online sales stop when the showing starts; the gate keeps selling to latecomers until it ends.
    // Showtime.Screen.Theater must be loaded.
    private string? NotOnSaleReason(Showtime showtime, PriceSchedule prices, bool atGate = false)
    {
        if (!showtime.Screen!.Theater!.IsActive)
            return atGate ? "This theater is inactive, so it can't sell tickets." : "This theater isn't selling tickets online.";
        if (atGate && showtime.EndsAt <= time.GetUtcNow())
            return "This showing has ended.";
        if (!atGate && showtime.StartsAt <= time.GetUtcNow())
            return "This showing has started, so tickets are no longer sold online.";
        if (!ProcessorFor(showtime.Screen.Theater).IsAvailable)
            return atGate ? "Card payments aren't set up yet, so tickets can't be sold at the gate."
                : "Online ticket sales aren't available yet.";
        if (NeedsPayoutAccount(showtime.Screen.Theater))
            return atGate ? "The theater's payout account isn't set up yet (Manage → Payouts), so tickets can't be sold at the gate."
                : "This theater isn't selling tickets online yet.";
        if (prices.Options.Count == 0)
            return "Tickets for this showing aren't on sale yet.";
        return null;
    }

    private string? AdmitProblem(Ticket ticket)
    {
        var showtime = ticket.Showtime!;
        var theater = showtime.Screen!.Theater!;
        var now = time.GetUtcNow();
        var starts = TheaterTime.ToLocal(theater, showtime.StartsAt);
        if (ticket.AdmittedAt is DateTimeOffset at)
            return $"Already used: admitted {TheaterTime.ToLocal(theater, at):ddd, MMM d h:mm tt}.";
        if (now >= showtime.EndsAt)
            return $"Not valid: this ticket was for {starts:ddd, MMM d} and can't be used on a later date.";
        if (now < showtime.StartsAt.AddHours(-AdmitOpensHoursBefore))
            return DateOnly.FromDateTime(starts) != DateOnly.FromDateTime(TheaterTime.ToLocal(theater, now))
                ? $"Not valid today: this ticket is for {starts:ddd, MMM d} at {starts:h:mm tt}."
                : $"Too early: this ticket is for the {starts:h:mm tt} showing, and gates open " +
                  $"{starts.AddHours(-AdmitOpensHoursBefore):h:mm tt}.";
        return null;
    }

    private static async Task<PriceSchedule> LoadPricesAsync(ApplicationDbContext db, Showtime showtime)
    {
        var schedules = db.PriceSchedules.AsNoTracking().Include(s => s.Options).Where(s => s.TheaterId == showtime.Screen!.TheaterId);
        var schedule = await (showtime.PriceScheduleId is int id ? schedules.Where(s => s.Id == id) : schedules.Where(s => s.IsDefault))
            .FirstOrDefaultAsync() ?? new PriceSchedule();
        schedule.Options.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
        return schedule;
    }

    private static async Task<Showtime> WithFeaturesAsync(ApplicationDbContext db, Showtime showtime)
    {
        await db.Entry(showtime).Collection(s => s.Features).Query().Include(f => f.Film).LoadAsync();
        return showtime;
    }

    private static IQueryable<Ticket> TicketsWithShowings(ApplicationDbContext db) =>
        db.Tickets.AsNoTracking()
            .Include(t => t.AddOns)
            .Include(t => t.Showtime!.Screen!.Theater)
            .Include(t => t.Showtime!.Features).ThenInclude(f => f.Film);

    private static async Task<TicketView> LoadViewAsync(ApplicationDbContext db, int ticketId) =>
        ToView(await TicketsWithShowings(db).SingleAsync(t => t.Id == ticketId));

    private static TicketView ToView(Ticket ticket)
    {
        var theater = ticket.Showtime!.Screen!.Theater!;
        ticket.AddOns.Sort((a, b) => a.Position.CompareTo(b.Position));
        return new TicketView(ticket, theater, ScheduleService.ToView(theater, ticket.Showtime),
            TheaterTime.ToLocal(theater, ticket.SoldAt!.Value),
            ticket.AdmittedAt is DateTimeOffset at ? TheaterTime.ToLocal(theater, at) : null);
    }

    private static HoldView ToHold(Ticket t) => new(t.Id, t.Row, t.Spot, t.SpotLabel, t.HeldUntil!.Value, t.VehicleSize);

    // The spot's label, if it's on the screen and fits the vehicle.
    private static string CheckSpot(Screen screen, int row, int spot, VehicleSize vehicle)
    {
        if (!Enum.IsDefined(vehicle))
            throw new AppValidationException("Choose what you're driving.");
        if (!screen.Contains(row, spot))
            throw new NotFoundException("That spot isn't on this screen.");
        var label = SpotLabels.Spot(screen.LabelScheme, row, spot);
        if (!screen.Fits(row, spot, vehicle))
            throw new AppValidationException(screen.LargeSpots.Count == 0
                ? $"Spot {label} is for cars and other standard vehicles, and this screen has no spots for large vehicles."
                : $"Spot {label} is for cars and other standard vehicles. Large vehicles park in the spots marked L, so they don't block the view.");
        return label;
    }

    private async Task<bool> TrySendReceiptAsync(TicketView view, string baseUri)
    {
        try
        {
            var receipt = TicketReceipt.Build(view, baseUri);
            await email.SendAsync(view.Ticket.Email!, receipt.Subject, receipt.Html, receipt.Images);
            return true;
        }
        catch (Exception ex)
        {
            // The sale stands; the ticket is under My tickets and the receipt can be sent again from there.
            logger.LogError(ex, "Couldn't email the receipt for ticket {TicketId}", view.Ticket.Id);
            return false;
        }
    }

    private static AppValidationException Taken(string label) =>
        new($"Sorry, someone else just took spot {label}. Please choose another spot.");

    private static bool IsUniqueViolation(DbUpdateException ex) => DbErrors.IsUniqueViolation(ex);

    // A gate code not used by another of the theater's tickets for a showing that hasn't ended. There are ~700,000
    // codes, so a clash is rare; a gate lookup still copes with duplicates by listing every match.
    private static async Task<string> NewShortCodeAsync(ApplicationDbContext db, int theaterId, DateTimeOffset now)
    {
        for (var attempt = 0; ; attempt++)
        {
            var code = ShortCodes.New();
            if (attempt >= 10 || !await db.Tickets.AnyAsync(t => t.ShortCode == code
                    && t.Showtime!.Screen!.TheaterId == theaterId && t.Showtime.EndsAt > now))
                return code;
        }
    }

    // 128 random bits, URL-safe.
    private static string NewCode() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
}

// What a ticket costs: the option's price, plus fees, minus discounts (percent discounts are off the option's price),
// never below zero.
public sealed record TicketQuote(PriceOption Option, List<(AddOn AddOn, decimal Effect)> Lines, decimal Total)
{
    public static TicketQuote For(PriceOption option, IEnumerable<AddOn> addOns)
    {
        var lines = addOns.Select(a => (a, a.Kind switch
        {
            AddOnKind.Fee => a.Amount,
            AddOnKind.Discount => -a.Amount,
            AddOnKind.PercentDiscount => -Math.Round(option.Price * a.Amount / 100m, 2, MidpointRounding.AwayFromZero),
            _ => 0m,
        })).ToList();
        return new TicketQuote(option, lines, Math.Max(0m, option.Price + lines.Sum(l => l.Item2)));
    }
}

// Open seat maps subscribe to hear when a showing's spots change (held, released, sold). In-process: the app runs as
// a single server; running several would need a shared bus (e.g. Postgres LISTEN/NOTIFY) instead.
public sealed class SpotEvents
{
    public event Action<int>? Changed;

    public void Publish(int showtimeId)
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<int>)handler)(showtimeId);
            }
            catch
            {
                // One broken subscriber (e.g. a closing circuit) mustn't stop the others hearing about it.
            }
        }
    }
}

// Releases expired holds every few seconds so everyone's seat map shows the spot free again promptly.
public sealed class HoldExpiryService(IServiceScopeFactory scopes, TimeProvider time, DriveInMetrics metrics,
    ILogger<HoldExpiryService> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<TicketSalesService>().ReleaseExpiredHoldsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Couldn't release expired ticket holds");
                metrics.JobFailed("hold_expiry");
            }
        }
    }
}
