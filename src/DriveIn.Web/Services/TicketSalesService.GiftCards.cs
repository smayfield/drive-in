using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

// Whether a theater is selling gift cards right now, for the purchase page.
// Payment is how the page takes a card for this theater.
public sealed record GiftCardOffer(Theater Theater, string? NotAvailableReason, PaymentClient Payment)
{
    public bool Available => NotAvailableReason is null;
}

// PaymentMethodId: the token the buyer's browser made for their card; the card itself never reaches the server.
public sealed record GiftCardPurchaseInput(decimal Amount, string? RecipientName, string? RecipientEmail, string? Message, string? PaymentMethodId);

// Card carries the full code: it's shown once to the buyer, who is also emailed it.
public sealed record GiftCardPurchaseResult(GiftCard Card, bool BuyerEmailed, bool? RecipientEmailed);

// What a shopper is told about a gift card they've presented: enough to know what's left, not the code back.
public sealed record GiftCardBalance(string Last4, decimal Balance);

// Received: sent to the user by someone else, rather than bought by them.
public sealed record MyGiftCard(GiftCard Card, string TheaterName, string TheaterSlug, DateTime PurchasedLocal, bool Received);

public sealed record GiftCardRow(int Id, string Last4, DateTime PurchasedLocal, string? PurchaserEmail, string? RecipientName,
    decimal InitialAmount, decimal Balance, bool IsTest);

public sealed record GiftCardSummary(List<GiftCardRow> Rows, int Count, decimal TotalSold, decimal Outstanding);

// Gift cards. A theater turns them on (ManageGiftCards); any signed-in user who can browse it buys one online with a
// card, and the code is emailed. A card is a bearer instrument: anyone holding the code (the buyer, the recipient, or
// whoever they pass it to) can spend it, and nothing checks who bought it. Presenting the code as payment, online or at the gate (which needs SellAtGate as
// selling always does), takes the card's balance off the ticket's total and keeps what's left for next time. A card is
// only good at the theater that sold it. Staff with ViewGiftCards see the sales, never the codes.
public sealed partial class TicketSalesService
{
    public const int MaxGiftCardRows = 500;

    // --- Buying ---

    public async Task<GiftCardOffer> GetGiftCardOfferAsync(ClaimsPrincipal user, string slug)
    {
        Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == slug && t.IsActive)
            ?? throw new NotFoundException("Theater not found.");
        if (!TheaterService.CanBrowse(user, theater))
            throw new NotFoundException("Theater not found.");
        return new GiftCardOffer(theater, GiftCardsNotAvailableReason(theater), ProcessorFor(theater).Client);
    }

    // Charges the buyer's card and issues a gift card, emailing its code to the buyer and, if given, the recipient.
    public async Task<GiftCardPurchaseResult> PurchaseGiftCardAsync(ClaimsPrincipal user, int theaterId, GiftCardPurchaseInput input, string baseUri)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Id == theaterId && t.IsActive)
            ?? throw new NotFoundException("Theater not found.");
        if (!TheaterService.CanBrowse(user, theater))
            throw new NotFoundException("Theater not found.");
        if (GiftCardsNotAvailableReason(theater) is string reason)
            throw new AppValidationException(reason);

        var amount = input.Amount;
        if (amount is < GiftCard.MinAmount or > GiftCard.MaxAmount || decimal.Round(amount, 2) != amount)
            throw new AppValidationException($"Choose an amount from {Money.Format(GiftCard.MinAmount)} to {Money.Format(GiftCard.MaxAmount)}.");
        var recipientName = Clean(input.RecipientName);
        var recipientEmail = Clean(input.RecipientEmail);
        var message = Clean(input.Message);
        if (recipientName is { Length: > 100 })
            throw new AppValidationException("The recipient's name can be at most 100 characters.");
        if (recipientEmail is not null && (recipientEmail.Length > 256 || !new EmailAddressAttribute().IsValid(recipientEmail)))
            throw new AppValidationException("The recipient's email address doesn't look right.");
        if (message is { Length: > 500 })
            throw new AppValidationException("The message can be at most 500 characters.");
        var buyer = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.Email, u.DisplayName }).FirstOrDefaultAsync();
        var buyerEmail = buyer?.Email?.Trim();
        if (string.IsNullOrEmpty(buyerEmail))
            throw new AppValidationException("Your account needs an email address to receive the gift card.");
        var now = time.GetUtcNow();
        var paymentMethod = PaymentTokens.Require(input.PaymentMethodId);
        var code = await NewGiftCardCodeAsync(db);

        PaymentResult result;
        try
        {
            // One key per purchase attempt: a retry inside the processor's client can't charge twice, and buying another
            // card is a new charge.
            var charge = new PaymentRequest(PaymentRequest.ToCents(amount), paymentOptions.Value.Currency, $"{theater.Name}: gift card",
                paymentMethod, $"giftcard-{Guid.NewGuid():N}",
                new Dictionary<string, string> { ["kind"] = "gift_card", ["theater_id"] = theater.Id.ToString() });
            result = await ProcessorFor(theater).ChargeAsync(charge);
        }
        catch
        {
            metrics.Payment("gift_card", "error", theater.IsDemo);
            throw;
        }
        metrics.Payment("gift_card", result.Approved ? "approved" : "declined", theater.IsDemo);
        if (!result.Approved)
            throw new AppValidationException($"Your payment wasn't approved: {result.DeclineReason ?? "declined"}. Check your card details or try another card.");

        var giftCard = new GiftCard
        {
            TheaterId = theater.Id, Code = code, InitialAmount = amount, Balance = amount, PurchasedAt = now,
            PurchaserId = userId, PurchaserEmail = buyerEmail, RecipientName = recipientName, RecipientEmail = recipientEmail,
            Message = message, CardBrand = result.CardBrand, CardLast4 = result.CardLast4,
            PaymentReference = result.Reference, IsTest = theater.IsDemo,
        };
        giftCard.Transactions.Add(new GiftCardTransaction
        {
            Kind = GiftCardTransactionKind.Purchase, Amount = amount, BalanceAfter = amount, At = now,
        });
        db.GiftCards.Add(giftCard);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync();
                break;
            }
            catch (DbUpdateException ex) when (IsGiftCardCodeClash(ex) && attempt < 5)
            {
                // Another card took this code between the check and the save. The buyer has paid, so pick another
                // rather than fail; the unique index means two cards can never share a code.
                giftCard.Code = await NewGiftCardCodeAsync(db);
            }
            catch (Exception ex)
            {
                // The card has been charged, so leave a trail for support to issue it by hand.
                logger.LogError(ex, "Charged {Amount} for a gift card at theater {TheaterId} but couldn't save it (payment {Reference})",
                    amount, theater.Id, result.Reference);
                throw;
            }
        }

        metrics.GiftCardSold(giftCard.IsTest, amount);

        var buyerEmailed = await TrySendGiftCardAsync(buyerEmail, giftCard, theater, baseUri, buyer?.DisplayName, forRecipient: false);
        bool? recipientEmailed = recipientEmail is null ? null
            : await TrySendGiftCardAsync(recipientEmail, giftCard, theater, baseUri, buyer?.DisplayName, forRecipient: true);
        return new GiftCardPurchaseResult(giftCard, buyerEmailed, recipientEmailed);
    }

    // The gift cards the user bought, and those sent to their (confirmed) email address, newest first, so a lost email
    // isn't a lost card for the buyer or the recipient.
    public async Task<List<MyGiftCard>> ListMyGiftCardsAsync(ClaimsPrincipal user)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var email = await db.Users.AsNoTracking().Where(u => u.Id == userId && u.EmailConfirmed)
            .Select(u => u.Email).FirstOrDefaultAsync();
        var lowered = email is null ? null : Guard.NormalizeEmail(email);
        var cards = await db.GiftCards.AsNoTracking().Include(g => g.Theater)
            .Where(g => g.PurchaserId == userId || (lowered != null && g.RecipientEmail != null && g.RecipientEmail.ToLower() == lowered))
            .OrderByDescending(g => g.PurchasedAt).ToListAsync();
        return cards.Select(g => new MyGiftCard(g, g.Theater!.Name, g.Theater.Slug, TheaterTime.ToLocal(g.Theater, g.PurchasedAt),
            Received: g.PurchaserId != userId)).ToList();
    }

    // --- Spending ---

    // What's left on a gift card a buyer has typed in, so checkout can show what will be charged to their card. Any
    // signed-in user who can browse the showing's theater may ask; a code that isn't that theater's is "not valid".
    public async Task<GiftCardBalance> CheckGiftCardAsync(ClaimsPrincipal user, int showtimeId, string code)
    {
        Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await TheaterOfShowtimeAsync(db, showtimeId);
        if (!TheaterService.CanBrowse(user, theater))
            throw new NotFoundException("Showing not found.");
        return await CheckGiftCardAsync(db, theater, code, ActionRateLimiter.KeyFor(user));
    }

    public async Task<GiftCardBalance> CheckGiftCardAtGateAsync(ClaimsPrincipal user, int showtimeId, string code)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await TheaterOfShowtimeAsync(db, showtimeId);
        await auth.RequireAsync(user, theater, TheaterPermissions.SellAtGate);
        return await CheckGiftCardAsync(db, theater, code, ActionRateLimiter.KeyFor(user));
    }

    private async Task<GiftCardBalance> CheckGiftCardAsync(ApplicationDbContext db, Theater theater, string code, string limitKey)
    {
        var card = await FindGiftCardAsync(db, theater.Id, code, forUpdate: false, limitKey: limitKey);
        return new GiftCardBalance(card.Last4, card.Balance);
    }

    // The theater's gift card for a typed code. A bad code and another theater's code get the same answer, so this can't
    // be used to find out which codes exist. Codes that match nothing are counted against the person trying them
    // (limitKey) and the theater; past either limit, no code is looked up until the window ends.
    private async Task<GiftCard> FindGiftCardAsync(ApplicationDbContext db, int theaterId, string? code, bool forUpdate, string limitKey)
    {
        var theaterKey = ActionRateLimiter.KeyForTheater(theaterId);
        limiter.Check(RateLimitPolicies.GiftCardMissesPerUser, limitKey);
        limiter.Check(RateLimitPolicies.GiftCardMissesPerTheater, theaterKey);
        var normalized = GiftCardCodes.Normalize(code);
        var cards = forUpdate ? db.GiftCards : db.GiftCards.AsNoTracking();
        var card = normalized is null ? null : await cards.FirstOrDefaultAsync(g => g.Code == normalized && g.TheaterId == theaterId);
        if (card is null)
        {
            limiter.Miss(RateLimitPolicies.GiftCardMissesPerUser, limitKey);
            limiter.Miss(RateLimitPolicies.GiftCardMissesPerTheater, theaterKey);
            throw new AppValidationException("That gift card isn't valid at this theater. Check the code and try again.");
        }
        if (card.Balance <= 0)
            throw new AppValidationException($"The gift card ending {card.Last4} has no balance left.");
        return card;
    }

    // Takes the amount off the card's balance and records it against the ticket. Saved along with the ticket going to
    // Paying: the card's Stamp makes a concurrent spend fail instead of overdrawing it.
    private static void SpendGiftCard(ApplicationDbContext db, GiftCard gift, Ticket ticket, decimal amount, DateTimeOffset now)
    {
        gift.Balance -= amount;
        gift.Stamp = Guid.NewGuid();
        db.GiftCardTransactions.Add(new GiftCardTransaction
        {
            GiftCard = gift, Kind = GiftCardTransactionKind.Redeem, Amount = -amount, BalanceAfter = gift.Balance,
            TicketId = ticket.Id, At = now,
        });
        ticket.GiftCardId = gift.Id;
        ticket.GiftCardLast4 = gift.Last4;
        ticket.GiftCardAmount = amount;
    }

    // The rest of the payment fell through (declined, or the processor failed): the spot goes back to Held and any gift
    // card money is put back, in one save so it can't be lost half way. Done in a fresh context, retrying if someone
    // else spends the card meanwhile.
    private async Task AbortPaymentAsync(ApplicationDbContext db, Ticket ticket, GiftCard? gift, decimal giftAmount)
    {
        if (gift is null)
        {
            await BackToHeldAsync(db, ticket);
            return;
        }
        for (var attempt = 1; ; attempt++)
        {
            await using var fresh = await dbFactory.CreateDbContextAsync();
            var t = await fresh.Tickets.FirstAsync(x => x.Id == ticket.Id);
            var g = await fresh.GiftCards.FirstAsync(x => x.Id == gift.Id);
            g.Balance += giftAmount;
            g.Stamp = Guid.NewGuid();
            fresh.GiftCardTransactions.Add(new GiftCardTransaction
            {
                GiftCardId = g.Id, Kind = GiftCardTransactionKind.Restore, Amount = giftAmount, BalanceAfter = g.Balance,
                TicketId = t.Id, At = time.GetUtcNow(),
            });
            t.GiftCardId = null;
            t.GiftCardLast4 = null;
            t.GiftCardAmount = 0;
            t.Status = TicketStatus.Held;
            t.Stamp = Guid.NewGuid();
            try
            {
                await fresh.SaveChangesAsync();
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 5)
            {
            }
        }
    }

    // --- Staff ---

    // The theater's gift card sales, newest first (up to MaxGiftCardRows), with the totals sold and still owed.
    public async Task<GiftCardSummary> ListGiftCardsAsync(ClaimsPrincipal user, int theaterId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await FindTheaterAsync(db, theaterId);
        await auth.RequireAsync(user, theater, TheaterPermissions.ViewGiftCards);
        var cards = db.GiftCards.AsNoTracking().Where(g => g.TheaterId == theaterId);
        var rows = (await cards.OrderByDescending(g => g.PurchasedAt).ThenByDescending(g => g.Id).Take(MaxGiftCardRows).ToListAsync())
            .Select(g => new GiftCardRow(g.Id, g.Last4, TheaterTime.ToLocal(theater, g.PurchasedAt), g.PurchaserEmail, g.RecipientName,
                g.InitialAmount, g.Balance, g.IsTest))
            .ToList();
        return new GiftCardSummary(rows, await cards.CountAsync(),
            await cards.SumAsync(g => (decimal?)g.InitialAmount) ?? 0m, await cards.SumAsync(g => (decimal?)g.Balance) ?? 0m);
    }

    // --- Helpers ---

    private string? GiftCardsNotAvailableReason(Theater theater) =>
        !theater.GiftCardsEnabled ? "This theater isn't selling gift cards."
        : !ProcessorFor(theater).IsAvailable ? "Online payment isn't available yet, so gift cards can't be sold."
        : null;

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // Makes the codes for new gift cards; tests swap it to force a clash.
    internal Func<string> NewGiftCardCode { get; set; } = GiftCardCodes.New;

    private async Task<string> NewGiftCardCodeAsync(ApplicationDbContext db)
    {
        for (var attempt = 0; ; attempt++)
        {
            var code = NewGiftCardCode();
            // ~78 random bits: a clash is not going to happen, but the unique index on the code is the real
            // guard, and PurchaseGiftCardAsync retries the save with a new code if it trips.
            if (attempt >= 5 || !await db.GiftCards.AnyAsync(g => g.Code == code))
                return code;
        }
    }

    // The code is the only unique column this save can clash on.
    private static bool IsGiftCardCodeClash(DbUpdateException ex) => IsUniqueViolation(ex);

    private async Task<bool> TrySendGiftCardAsync(string to, GiftCard card, Theater theater, string baseUri, string? fromName, bool forRecipient)
    {
        try
        {
            var mail = GiftCardEmail.Build(card, theater, baseUri, fromName, forRecipient);
            await email.SendAsync(to, mail.Subject, mail.Html);
            return true;
        }
        catch (Exception ex)
        {
            // The sale stands; the code is on screen and under My tickets.
            logger.LogError(ex, "Couldn't email gift card {GiftCardId}", card.Id);
            return false;
        }
    }
}
