using DriveIn.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

// What a reconciliation pass did: sales finished, sales undone, and charges still waiting on the processor.
public sealed record ReconcileSummary(int Completed, int Released, int Waiting);

// Finishing or undoing a charge, the same way whoever hears its outcome: the checkout itself (inline), a processor
// webhook, or PaymentReconcileService asking the processor about a charge whose outcome the server never heard (it
// timed out, or the server died mid-charge). Every step is idempotent and checks that the sale is still waiting under
// the same payment key, so the three can race without selling a spot twice, issuing two gift cards, or putting gift
// card money back twice.
public sealed partial class TicketSalesService
{
    // A charge this old with no outcome is asked about...
    public static readonly TimeSpan ReconcileAfter = TimeSpan.FromMinutes(5);

    // ...and given up on if the processor still has no record of it this long after it started (it never got there).
    // Longer than Stripe's search can lag behind a new charge (about a minute).
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(30);

    // --- Tickets ---

    // Sells a Paying ticket whose charge went through. Returns the ticket and whether this call sold it; null if it
    // isn't waiting under this key (e.g. it was undone).
    internal async Task<(Ticket? Ticket, bool CompletedHere)> CompletePaidTicketAsync(int ticketId, string? paymentKey, PaymentResult result)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId && t.PaymentKey == paymentKey);
            if (ticket?.Status == TicketStatus.Sold)
                return (ticket, false);
            if (ticket?.Status != TicketStatus.Paying)
                return (null, false);
            var now = time.GetUtcNow();
            ticket.Status = TicketStatus.Sold;
            ticket.HeldUntil = null;
            ticket.SoldAt = now;
            ticket.PaymentReference = result.Reference;
            ticket.Code = NewCode();
            if (ticket.SoldAtGate)
            {
                ticket.UserId = null;
                ticket.AdmittedAt = now;
            }
            else
            {
                ticket.CardBrand = result.CardBrand;
                ticket.CardLast4 = result.CardLast4;
            }
            ticket.Stamp = Guid.NewGuid();
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                continue; // finished (or undone) by someone else meanwhile: look again
            }
            events.Publish(ticket.ShowtimeId);
            metrics.TicketSold(ticket.SoldAtGate ? DriveInMetrics.Gate : DriveInMetrics.Online, ticket.IsTest, ticket.Total);
            if (ticket.SoldAtGate)
                metrics.TicketAdmitted("sold_at_gate");
            return (ticket, true);
        }
    }

    // Undoes a Paying ticket whose charge fell through: back to Held (once its hold has run out it's swept and the spot
    // goes back on sale), the sale's details cleared, and any gift card money put back, in one save so nothing is lost
    // half way. Returns false if it isn't waiting under this key. Retries if someone spends the gift card meanwhile.
    internal async Task<bool> AbortTicketPaymentAsync(int ticketId, string? paymentKey)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var ticket = await db.Tickets.Include(t => t.AddOns)
                .FirstOrDefaultAsync(t => t.Id == ticketId && t.Status == TicketStatus.Paying && t.PaymentKey == paymentKey);
            if (ticket is null)
                return false;
            if (ticket.GiftCardId is int giftId && ticket.GiftCardAmount > 0)
            {
                var gift = await db.GiftCards.FirstAsync(g => g.Id == giftId);
                gift.Balance += ticket.GiftCardAmount;
                gift.Stamp = Guid.NewGuid();
                db.GiftCardTransactions.Add(new GiftCardTransaction
                {
                    GiftCardId = gift.Id, Kind = GiftCardTransactionKind.Restore, Amount = ticket.GiftCardAmount,
                    BalanceAfter = gift.Balance, TicketId = ticket.Id, At = time.GetUtcNow(),
                });
            }
            ticket.GiftCardId = null;
            ticket.GiftCardLast4 = null;
            ticket.GiftCardAmount = 0;
            ticket.AddOns.Clear();
            ticket.OptionName = null;
            ticket.OptionPrice = 0;
            ticket.Total = 0;
            ticket.ShortCode = null;
            ticket.Email = null;
            ticket.SoldAtGate = false;
            ticket.SoldById = null;
            ticket.IsTest = false;
            ticket.PaymentKey = null;
            ticket.PaymentStartedAt = null;
            ticket.Status = TicketStatus.Held;
            ticket.Stamp = Guid.NewGuid();
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException) when (attempt < 5)
            {
                continue;
            }
            events.Publish(ticket.ShowtimeId);
            return true;
        }
    }

    // --- Gift cards ---

    // Issues the gift card for a Paying purchase whose charge went through, and emails its code. Returns null if the
    // purchase isn't waiting (it failed); if it was already completed, returns that card.
    internal async Task<GiftCardPurchaseResult?> CompleteGiftCardPurchaseAsync(int purchaseId, PaymentResult result, string baseUri)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var purchase = await db.GiftCardPurchases.Include(p => p.Theater).Include(p => p.GiftCard)
            .FirstOrDefaultAsync(p => p.Id == purchaseId);
        if (purchase is { Status: GiftCardPurchaseStatus.Completed, GiftCard: not null })
            return AlreadyIssued(purchase);
        if (purchase?.Status != GiftCardPurchaseStatus.Paying)
            return null;

        var giftCard = new GiftCard
        {
            TheaterId = purchase.TheaterId, Code = await NewGiftCardCodeAsync(db), InitialAmount = purchase.Amount, Balance = purchase.Amount,
            PurchasedAt = purchase.StartedAt, PurchaserId = purchase.PurchaserId, PurchaserEmail = purchase.PurchaserEmail,
            RecipientName = purchase.RecipientName, RecipientEmail = purchase.RecipientEmail, Message = purchase.Message,
            CardBrand = result.CardBrand, CardLast4 = result.CardLast4, PaymentReference = result.Reference, IsTest = purchase.IsTest,
        };
        giftCard.Transactions.Add(new GiftCardTransaction
        {
            Kind = GiftCardTransactionKind.Purchase, Amount = purchase.Amount, BalanceAfter = purchase.Amount, At = purchase.StartedAt,
        });
        db.GiftCards.Add(giftCard);
        purchase.GiftCard = giftCard;
        purchase.Status = GiftCardPurchaseStatus.Completed;
        purchase.Stamp = Guid.NewGuid();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync();
                break;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Completed by a webhook or the reconciler meanwhile (this save, card included, didn't happen).
                await using var fresh = await dbFactory.CreateDbContextAsync();
                var done = await fresh.GiftCardPurchases.AsNoTracking().Include(p => p.GiftCard)
                    .FirstAsync(p => p.Id == purchaseId);
                return done is { Status: GiftCardPurchaseStatus.Completed, GiftCard: not null } ? AlreadyIssued(done) : null;
            }
            catch (DbUpdateException ex) when (IsGiftCardCodeClash(ex) && attempt < 5)
            {
                // Another card took this code between the check and the save. The buyer has paid, so pick another
                // rather than fail; the unique index means two cards can never share a code.
                giftCard.Code = await NewGiftCardCodeAsync(db);
            }
            catch (Exception ex)
            {
                // The card has been charged; the purchase stays Paying, so the reconciler tries again (and the log
                // leaves a trail for support).
                logger.LogError(ex, "Charged {Amount} for gift card purchase {PurchaseId} but couldn't issue the card (payment {Reference})",
                    purchase.Amount, purchase.Id, result.Reference);
                throw;
            }
        }

        metrics.GiftCardSold(giftCard.IsTest, giftCard.InitialAmount);
        var theater = purchase.Theater!;
        var buyerEmailed = purchase.PurchaserEmail is not null
            && await TrySendGiftCardAsync(purchase.PurchaserEmail, giftCard, theater, baseUri, purchase.PurchaserName, forRecipient: false);
        bool? recipientEmailed = purchase.RecipientEmail is null ? null
            : await TrySendGiftCardAsync(purchase.RecipientEmail, giftCard, theater, baseUri, purchase.PurchaserName, forRecipient: true);
        return new GiftCardPurchaseResult(giftCard, buyerEmailed, recipientEmailed);
    }

    // Whoever issued it emailed it.
    private static GiftCardPurchaseResult AlreadyIssued(GiftCardPurchase purchase) =>
        new(purchase.GiftCard!, true, purchase.RecipientEmail is null ? null : true);

    // The purchase's charge fell through: nothing to undo, since the card is only issued once it's paid.
    internal async Task<bool> FailGiftCardPurchaseAsync(int purchaseId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var purchase = await db.GiftCardPurchases.FirstOrDefaultAsync(p => p.Id == purchaseId && p.Status == GiftCardPurchaseStatus.Paying);
        if (purchase is null)
            return false;
        purchase.Status = GiftCardPurchaseStatus.Failed;
        purchase.Stamp = Guid.NewGuid();
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            return false; // completed or failed meanwhile
        }
        return true;
    }

    // --- Reconciling ---

    // Settles charges whose outcome the server never heard: Paying tickets and gift card purchases at least
    // ReconcileAfter old are looked up with their processor by payment key. Succeeded: the sale is finished (and the
    // receipt or gift card emailed). Failed, or still unknown to the processor after GiveUpAfter: undone (the spot goes
    // back on sale, gift card money is put back). Anything else waits for the next pass. Run by PaymentReconcileService.
    public async Task<ReconcileSummary> ReconcilePaymentsAsync(string baseUri, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var cutoff = now - ReconcileAfter;
        List<Ticket> tickets;
        List<GiftCardPurchase> purchases;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            tickets = await db.Tickets.AsNoTracking().Include(t => t.Showtime!.Screen!.Theater)
                .Where(t => t.Status == TicketStatus.Paying && t.PaymentKey != null && t.PaymentStartedAt <= cutoff)
                .ToListAsync(ct);
            purchases = await db.GiftCardPurchases.AsNoTracking().Include(p => p.Theater)
                .Where(p => p.Status == GiftCardPurchaseStatus.Paying && p.StartedAt <= cutoff)
                .ToListAsync(ct);
        }

        int completed = 0, released = 0, waiting = 0;
        void Count(string? outcome)
        {
            if (outcome == Completed) completed++;
            else if (outcome == Released) released++;
            else waiting++;
        }
        foreach (var ticket in tickets)
        {
            try
            {
                var theater = ticket.Showtime!.Screen!.Theater!;
                var giveUp = ticket.PaymentStartedAt <= now - GiveUpAfter;
                // Paid in full by gift card: there's no charge to ask about, so it can just be finished.
                var status = ticket.CardAmount == 0 ? new PaymentStatus(PaymentState.Succeeded, new PaymentResult(true, null))
                    : await ProcessorFor(theater).GetStatusAsync(new PaymentLookup(ticket.PaymentKey!), ct);
                Count(await SettleTicketAsync(ticket.Id, ticket.PaymentKey!, status, giveUp, baseUri, via: "job"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Couldn't reconcile the payment for ticket {TicketId} ({PaymentKey})", ticket.Id, ticket.PaymentKey);
                waiting++;
            }
        }
        foreach (var purchase in purchases)
        {
            try
            {
                var status = await ProcessorFor(purchase.Theater!).GetStatusAsync(new PaymentLookup(purchase.PaymentKey), ct);
                Count(await SettleGiftCardPurchaseAsync(purchase.Id, status, purchase.StartedAt <= now - GiveUpAfter, baseUri, via: "job"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Couldn't reconcile the payment for gift card purchase {PurchaseId}", purchase.Id);
                waiting++;
            }
        }
        return new ReconcileSummary(completed, released, waiting);
    }

    // A processor says something happened to the charge with this key (a webhook): ask the processor itself (the
    // event is only a nudge) and settle the sale now. Unknown keys are ignored, unless the charge succeeded, which means
    // someone paid for a sale that's no longer waiting: that's logged as an error for support.
    public async Task<string?> SettlePaymentAsync(string paymentKey, string? reference, string baseUri, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (paymentKey.StartsWith(GiftCardPurchase.KeyPrefix, StringComparison.Ordinal)
            && int.TryParse(paymentKey[GiftCardPurchase.KeyPrefix.Length..], out var purchaseId))
        {
            var purchase = await db.GiftCardPurchases.AsNoTracking().Include(p => p.Theater).FirstOrDefaultAsync(p => p.Id == purchaseId, ct);
            if (purchase is null)
                return null;
            var status = await ProcessorFor(purchase.Theater!).GetStatusAsync(new PaymentLookup(paymentKey, reference), ct);
            if (status.State == PaymentState.Succeeded && purchase.Status == GiftCardPurchaseStatus.Failed)
                logger.LogError("Payment {Reference} for gift card purchase {PurchaseId} succeeded after it was given up on; issue the card by hand",
                    reference, purchase.Id);
            return await SettleGiftCardPurchaseAsync(purchase.Id, status, giveUp: false, baseUri, via: "webhook");
        }

        var ticket = await db.Tickets.AsNoTracking().Include(t => t.Showtime!.Screen!.Theater)
            .FirstOrDefaultAsync(t => t.PaymentKey == paymentKey, ct);
        if (ticket is null)
        {
            // Undone tickets drop their key, so a success here was paid for after the sale was given up on.
            var late = await payments.GetStatusAsync(new PaymentLookup(paymentKey, reference), ct);
            if (late.State == PaymentState.Succeeded)
                logger.LogError("Payment {Reference} ({PaymentKey}) succeeded, but no ticket is waiting for it; refund it or sell the spot by hand",
                    reference, paymentKey);
            return null;
        }
        var ticketStatus = await ProcessorFor(ticket.Showtime!.Screen!.Theater!).GetStatusAsync(new PaymentLookup(paymentKey, reference), ct);
        return await SettleTicketAsync(ticket.Id, paymentKey, ticketStatus, giveUp: false, baseUri, via: "webhook");
    }

    private const string Completed = "completed";
    private const string Released = "released";

    private async Task<string?> SettleTicketAsync(int ticketId, string paymentKey, PaymentStatus status, bool giveUp, string baseUri, string via)
    {
        if (status is { State: PaymentState.Succeeded, Result: { } result })
        {
            var (ticket, completedHere) = await CompletePaidTicketAsync(ticketId, paymentKey, result);
            if (ticket is null || !completedHere)
                return null;
            if (!ticket.SoldAtGate && ticket.Email is not null)
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                await TrySendReceiptAsync(await LoadViewAsync(db, ticket.Id), baseUri);
            }
            metrics.PaymentReconciled("ticket", Completed, via);
            return Completed;
        }
        if (status.State == PaymentState.Failed || (status.State == PaymentState.NotFound && giveUp))
        {
            if (!await AbortTicketPaymentAsync(ticketId, paymentKey))
                return null;
            metrics.PaymentReconciled("ticket", Released, via);
            return Released;
        }
        return null;
    }

    private async Task<string?> SettleGiftCardPurchaseAsync(int purchaseId, PaymentStatus status, bool giveUp, string baseUri, string via)
    {
        if (status is { State: PaymentState.Succeeded, Result: { } result })
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            if (!await db.GiftCardPurchases.AnyAsync(p => p.Id == purchaseId && p.Status == GiftCardPurchaseStatus.Paying))
                return null;
            if (await CompleteGiftCardPurchaseAsync(purchaseId, result, baseUri) is null)
                return null;
            metrics.PaymentReconciled("gift_card", Completed, via);
            return Completed;
        }
        if (status.State == PaymentState.Failed || (status.State == PaymentState.NotFound && giveUp))
        {
            if (!await FailGiftCardPurchaseAsync(purchaseId))
                return null;
            metrics.PaymentReconciled("gift_card", Released, via);
            return Released;
        }
        return null;
    }
}

// Every minute, settles charges whose outcome the server never heard (TicketSalesService.ReconcilePaymentsAsync).
public sealed class PaymentReconcileService(IServiceScopeFactory scopes, TimeProvider time, DriveInMetrics metrics,
    Microsoft.Extensions.Options.IOptions<NotificationOptions> notifications, ILogger<PaymentReconcileService> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var summary = await scope.ServiceProvider.GetRequiredService<TicketSalesService>()
                    .ReconcilePaymentsAsync(notifications.Value.SiteUrl, stoppingToken);
                if (summary.Completed + summary.Released > 0)
                    logger.LogWarning("Reconciled payments: {Completed} finished, {Released} undone, {Waiting} still waiting",
                        summary.Completed, summary.Released, summary.Waiting);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Couldn't reconcile payments");
                metrics.JobFailed("payment_reconcile");
            }
        }
    }
}
