using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DriveIn.Web.Services;

// A live theater (or one with a subscription) on the admin subscriptions page. Subscription is null for a live theater
// that isn't billed yet: admin-created theaters, and theaters that went live before billing existed.
public sealed record BillingSubscriptionRow(int TheaterId, string TheaterName, string? OwnerEmail, int Screens, bool TheaterActive,
    Subscription? Subscription)
{
    public decimal? MonthlyTotal => Subscription is { } s ? s.PricePerScreenPerMonth * Screens : null;
}

public sealed record InvoiceFilter(InvoiceStatus? Status = null, DateOnly? Month = null, int? TheaterId = null, bool OverdueOnly = false);

// What a theater's Billing page shows. Drafts aren't included: the owner sees an invoice once it's issued.
public sealed record TheaterBilling(Subscription? Subscription, string? BillTo, List<Invoice> Invoices, DateOnly Today);

public sealed record IssueResult(int Issued, int NotEmailed);

public sealed record PaymentInput(decimal Amount, PaymentMethod Method, string? Reference, DateOnly ReceivedOn);

// Subscriptions and invoices for the Standard plan. A live theater's subscription bills screens × its locked-in price
// for each calendar month (in the theater's time zone) that its season touches, in full and in advance. The background
// job (BillingJobService) drafts those invoices; an admin reviews and issues them, which emails the bill-to address, and
// records payments as they arrive (there's no payment processor for this yet), which emails a receipt.
//
// Admin work checks Guard.RequireAdmin; what the owner sees and does checks billing.view / billing.manage.
// Billing dates (issued, due, overdue, received) are UTC dates; which month is billed follows the theater's time zone.
public sealed class BillingService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IAuthorizationService auth,
    IAppEmailSender email,
    IOptions<PlanOptions> plans,
    IOptions<BillingOptions> billing,
    IOptions<CompanyOptions> company,
    TimeProvider time,
    ILogger<BillingService> logger)
{
    public const decimal MaxLineAmount = 99_999.99m;
    public const int MaxLineQuantity = 1_000;

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    public DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    // --- Rules ---

    public static DateOnly MonthOf(DateOnly date) => new(date.Year, date.Month, 1);

    public static DateOnly LocalToday(Theater theater, DateTimeOffset now) => DateOnly.FromDateTime(TheaterTime.ToLocal(theater, now));

    // Whether the subscription bills this month (its first day): it had started by then and hadn't ended, the season
    // touches it, and the theater is live, active and has screens. Needs Theater.Screens loaded.
    public static bool IsBillable(Subscription sub, Theater theater, DateOnly month) =>
        theater is { IsActive: true, Mode: TheaterMode.Live } && theater.Screens.Count > 0
        && MonthOf(sub.StartedOn) <= month
        && (sub.EndsAfterMonth is not DateOnly end || month <= end)
        && Seasons.TouchesMonth(theater, month);

    internal static Subscription NewSubscription(Theater theater, decimal price, DateTimeOffset now) => new()
    {
        Theater = theater, TheaterId = theater.Id, PricePerScreenPerMonth = price, Status = SubscriptionStatus.Active,
        StartedOn = LocalToday(theater, now), CreatedAt = now, UpdatedAt = now,
    };

    // --- Subscriptions (admin) ---

    public async Task<List<BillingSubscriptionRow>> ListSubscriptionsAsync(ClaimsPrincipal user)
    {
        Guard.RequireAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var subs = await db.Subscriptions.AsNoTracking().ToDictionaryAsync(s => s.TheaterId);
        var theaters = await db.Theaters.AsNoTracking()
            .Where(t => t.Mode == TheaterMode.Live || db.Subscriptions.Any(s => s.TheaterId == t.Id))
            .OrderBy(t => t.Name)
            .Select(t => new { t.Id, t.Name, OwnerEmail = t.Owner != null ? t.Owner.Email : null, Screens = t.Screens.Count, t.IsActive })
            .ToListAsync();
        return theaters.Select(t => new BillingSubscriptionRow(t.Id, t.Name, t.OwnerEmail, t.Screens, t.IsActive,
            subs.GetValueOrDefault(t.Id))).ToList();
    }

    public async Task<Subscription?> GetSubscriptionAsync(ClaimsPrincipal user, int theaterId)
    {
        Guard.RequireAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.TheaterId == theaterId);
    }

    // Starts billing a live theater at this price per screen (the plan's current price when not given), from this month.
    public async Task StartSubscriptionAsync(ClaimsPrincipal user, int theaterId, decimal? pricePerScreen)
    {
        Guard.RequireAdmin(user);
        var price = RequirePrice(pricePerScreen ?? plans.Value.PricePerScreenPerMonth
            ?? throw new AppValidationException("Enter the price per screen (Plans:PricePerScreenPerMonth isn't set)."));
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.FirstOrDefaultAsync(t => t.Id == theaterId) ?? throw new NotFoundException("Theater not found.");
        if (theater.IsDemo)
            throw new AppValidationException("Demo theaters aren't billed. Activate the theater first.");
        if (await db.Subscriptions.AnyAsync(s => s.TheaterId == theaterId))
            throw new AppValidationException("This theater already has a subscription.");
        db.Subscriptions.Add(NewSubscription(theater, price, time.GetUtcNow()));
        await db.SaveChangesAsync();
        await GenerateDraftsCoreAsync(theaterId);
    }

    // Applies to invoices drafted from now on. The Terms promise owners 30 days' notice of a price change.
    public async Task ChangePriceAsync(ClaimsPrincipal user, int theaterId, decimal pricePerScreen)
    {
        Guard.RequireAdmin(user);
        var price = RequirePrice(pricePerScreen);
        await using var db = await dbFactory.CreateDbContextAsync();
        var sub = await RequireSubscriptionAsync(db, theaterId);
        sub.PricePerScreenPerMonth = price;
        sub.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync();
    }

    // The owner (billing.manage) or an admin. Billing stops after the current month (which is still billed if its
    // season is open, since months are billed in advance); invoices already issued stand.
    public async Task CancelAsync(ClaimsPrincipal user, int theaterId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.Include(t => t.Owner).FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");
        await auth.RequireAsync(user, theater, TheaterPermissions.ManageBilling);
        var sub = await RequireSubscriptionAsync(db, theaterId);
        if (!sub.IsActive)
            throw new AppValidationException("The subscription is already cancelled.");
        var now = time.GetUtcNow();
        var lastMonth = MonthOf(LocalToday(theater, now));
        sub.Status = SubscriptionStatus.Canceled;
        sub.EndsAfterMonth = lastMonth;
        sub.CanceledAt = now;
        sub.CanceledById = Guard.RequireUserId(user);
        sub.UpdatedAt = now;
        await db.SaveChangesAsync();

        var body = $"<p>The Standard plan subscription for {E(theater.Name)} is cancelled. {E(lastMonth.ToString("MMMM yyyy", Culture))} " +
            "is the last month billed, and invoices already issued are still due.</p>";
        await TrySendAsync(sub.BillingEmail ?? theater.Owner?.Email, $"Subscription cancelled for {theater.Name}", body);
        if (!user.IsAdmin())
            foreach (var admin in await OnboardingService.AdminEmailsAsync(db))
                await TrySendAsync(admin, $"{theater.Name} cancelled its subscription", body);
    }

    // Admin only. A subscription that had already ended starts again from this month.
    public async Task ReactivateAsync(ClaimsPrincipal user, int theaterId)
    {
        Guard.RequireAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var sub = await RequireSubscriptionAsync(db, theaterId);
        if (sub.IsActive)
            throw new AppValidationException("The subscription is already active.");
        var theater = await db.Theaters.FirstAsync(t => t.Id == theaterId);
        var now = time.GetUtcNow();
        var today = LocalToday(theater, now);
        if (sub.EndsAfterMonth is DateOnly end && end < MonthOf(today))
            sub.StartedOn = today;
        sub.Status = SubscriptionStatus.Active;
        sub.EndsAfterMonth = null;
        sub.CanceledAt = null;
        sub.CanceledById = null;
        sub.UpdatedAt = now;
        await db.SaveChangesAsync();
        await GenerateDraftsCoreAsync(theaterId);
    }

    // The owner (billing.manage) or an admin. Blank goes back to the owner's email.
    public async Task UpdateBillingEmailAsync(ClaimsPrincipal user, int theaterId, string? billingEmail)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.FirstOrDefaultAsync(t => t.Id == theaterId) ?? throw new NotFoundException("Theater not found.");
        await auth.RequireAsync(user, theater, TheaterPermissions.ManageBilling);
        var sub = await RequireSubscriptionAsync(db, theaterId);
        var address = string.IsNullOrWhiteSpace(billingEmail) ? null : billingEmail.Trim();
        if (address is not null && (address.Length > 256 || !new EmailAddressAttribute().IsValid(address)))
            throw new AppValidationException("Enter a valid email address.");
        sub.BillingEmail = address;
        sub.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync();
    }

    // --- Drafting ---

    // Admin: runs what the background job does now. Returns how many drafts were created.
    public async Task<int> GenerateDraftsAsync(ClaimsPrincipal user)
    {
        Guard.RequireAdmin(user);
        return await GenerateDraftsCoreAsync(null);
    }

    // The background job: drafts what's due and tells the admins when there's something to review.
    internal async Task<int> RunScheduledAsync()
    {
        var created = await GenerateDraftsCoreAsync(null);
        if (created > 0)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var body = $"<p>{created} new draft invoice{(created == 1 ? " is" : "s are")} ready to review and issue under Admin → Billing.</p>";
            foreach (var admin in await OnboardingService.AdminEmailsAsync(db))
                await TrySendAsync(admin, $"{created} draft invoice{(created == 1 ? "" : "s")} to review", body);
        }
        return created;
    }

    // Drafts this month's and last month's invoice (last month catches up after downtime) for every subscription that
    // bills them and doesn't have one yet. Safe to run at any time and concurrently: the unique index on subscription +
    // month is the real guard. No authorization: callers have checked.
    internal async Task<int> GenerateDraftsCoreAsync(int? theaterId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = time.GetUtcNow();
        var subs = await db.Subscriptions
            .Include(s => s.Theater!).ThenInclude(t => t.Screens)
            .Include(s => s.Theater!).ThenInclude(t => t.Owner)
            .Where(s => (s.Status == SubscriptionStatus.Active || s.EndsAfterMonth != null) && (theaterId == null || s.TheaterId == theaterId))
            .ToListAsync();
        var created = 0;
        foreach (var sub in subs)
        {
            var theater = sub.Theater!;
            var thisMonth = MonthOf(LocalToday(theater, now));
            foreach (var month in new[] { thisMonth.AddMonths(-1), thisMonth })
            {
                if (!IsBillable(sub, theater, month)
                    || await db.Invoices.AnyAsync(i => i.SubscriptionId == sub.Id && i.PeriodMonth == month && i.Status != InvoiceStatus.Void))
                    continue;
                var draft = NewDraft(sub, theater, month, now);
                db.Invoices.Add(draft);
                try
                {
                    await db.SaveChangesAsync();
                    created++;
                }
                catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
                {
                    // Another run drafted it first.
                    db.Entry(draft).State = EntityState.Detached;
                    foreach (var line in draft.Lines)
                        db.Entry(line).State = EntityState.Detached;
                }
            }
        }
        if (created > 0)
            logger.LogInformation("Drafted {Count} invoices", created);
        return created;
    }

    private Invoice NewDraft(Subscription sub, Theater theater, DateOnly month, DateTimeOffset now)
    {
        var screens = theater.Screens.Count;
        var invoice = new Invoice
        {
            SubscriptionId = sub.Id, TheaterId = theater.Id, PeriodMonth = month, Status = InvoiceStatus.Draft, CreatedAt = now,
            Lines =
            [
                new InvoiceLine
                {
                    SortOrder = 0, Quantity = screens, UnitPrice = sub.PricePerScreenPerMonth, Amount = screens * sub.PricePerScreenPerMonth,
                    Description = $"{sub.Plan} plan, {month.ToString("MMMM yyyy", Culture)}: {screens} screen{(screens == 1 ? "" : "s")}",
                },
            ],
        };
        FillBillTo(invoice, sub, theater);
        invoice.Total = invoice.Lines.Sum(l => l.Amount);
        return invoice;
    }

    private static void FillBillTo(Invoice invoice, Subscription? sub, Theater? theater)
    {
        if (theater is null)
            return;
        invoice.TheaterName = theater.Name;
        invoice.TheaterAddress = TicketReceipt.Address(theater) is { Length: > 0 } a ? a : null;
        invoice.BillToName = theater.Owner is { } owner ? (string.IsNullOrWhiteSpace(owner.DisplayName) ? owner.Email : owner.DisplayName) : null;
        invoice.BillToEmail = sub?.BillingEmail ?? theater.Owner?.Email;
    }

    // --- Invoices (admin) ---

    public async Task<List<Invoice>> ListInvoicesAsync(ClaimsPrincipal user, InvoiceFilter filter)
    {
        Guard.RequireAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.Invoices.AsNoTracking().Include(i => i.Payments).AsQueryable();
        if (filter.Status is InvoiceStatus status)
            query = query.Where(i => i.Status == status);
        if (filter.Month is DateOnly month)
        {
            var periodMonth = MonthOf(month);
            query = query.Where(i => i.PeriodMonth == periodMonth);
        }
        if (filter.TheaterId is int theaterId)
            query = query.Where(i => i.TheaterId == theaterId);
        if (filter.OverdueOnly)
        {
            var today = Today;
            query = query.Where(i => i.Status == InvoiceStatus.Issued && i.DueOn < today);
        }
        return await query.OrderByDescending(i => i.PeriodMonth).ThenBy(i => i.TheaterName).ThenByDescending(i => i.Id).ToListAsync();
    }

    public async Task<Invoice> GetInvoiceForAdminAsync(ClaimsPrincipal user, int invoiceId)
    {
        Guard.RequireAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await LoadInvoice(db.Invoices.AsNoTracking()).FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new NotFoundException("Invoice not found.");
    }

    // An adjustment or credit (negative unit price) on a draft.
    public async Task AddLineAsync(ClaimsPrincipal user, int invoiceId, string description, int quantity, decimal unitPrice)
    {
        Guard.RequireAdmin(user);
        description = (description ?? "").Trim();
        if (description.Length is 0 or > 200)
            throw new AppValidationException("Describe the line in 1 to 200 characters.");
        if (quantity is < 1 or > MaxLineQuantity)
            throw new AppValidationException($"The quantity must be 1 to {MaxLineQuantity}.");
        if (unitPrice == 0 || Math.Abs(unitPrice) > MaxLineAmount || decimal.Round(unitPrice, 2) != unitPrice)
            throw new AppValidationException($"Enter an amount in dollars and cents, up to {Money.Format(MaxLineAmount)} either way.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var invoice = await RequireDraftAsync(db, invoiceId);
        invoice.Lines.Add(new InvoiceLine
        {
            Description = description, Quantity = quantity, UnitPrice = unitPrice, Amount = quantity * unitPrice,
            SortOrder = invoice.Lines.Count == 0 ? 0 : invoice.Lines.Max(l => l.SortOrder) + 1,
        });
        invoice.Total = invoice.Lines.Sum(l => l.Amount);
        await db.SaveChangesAsync();
    }

    public async Task RemoveLineAsync(ClaimsPrincipal user, int invoiceId, int lineId)
    {
        Guard.RequireAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var invoice = await RequireDraftAsync(db, invoiceId);
        var line = invoice.Lines.FirstOrDefault(l => l.Id == lineId) ?? throw new NotFoundException("Line not found.");
        invoice.Lines.Remove(line);
        db.InvoiceLines.Remove(line);
        invoice.Total = invoice.Lines.Sum(l => l.Amount);
        await db.SaveChangesAsync();
    }

    public async Task<IssueResult> IssueAllDraftsAsync(ClaimsPrincipal user, string baseUri)
    {
        Guard.RequireAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var ids = await db.Invoices.Where(i => i.Status == InvoiceStatus.Draft).OrderBy(i => i.Id).Select(i => i.Id).ToListAsync();
        return await IssueAsync(user, ids, baseUri);
    }

    // Numbers each draft, sets its due date, and emails it to the bill-to address (refreshed from the subscription and
    // theater now, in case they changed since it was drafted). A $0 invoice is issued already paid. An email that
    // fails doesn't undo the issue; the result counts them so the admin can resend.
    public async Task<IssueResult> IssueAsync(ClaimsPrincipal user, IReadOnlyCollection<int> invoiceIds, string baseUri)
    {
        Guard.RequireAdmin(user);
        int issued = 0, notEmailed = 0;
        foreach (var id in invoiceIds)
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var invoice = await RequireDraftAsync(db, id);
            if (invoice.Total < 0)
                throw new AppValidationException($"The {invoice.TheaterName} invoice for {invoice.PeriodMonth:MMM yyyy} comes to less than $0. Adjust its lines first.");
            var sub = invoice.SubscriptionId is int subId ? await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == subId) : null;
            var theater = invoice.TheaterId is int theaterId
                ? await db.Theaters.Include(t => t.Owner).FirstOrDefaultAsync(t => t.Id == theaterId)
                : null;
            FillBillTo(invoice, sub, theater);
            var now = time.GetUtcNow();
            invoice.IssuedAt = now;
            invoice.DueOn = DateOnly.FromDateTime(now.UtcDateTime).AddDays(Math.Max(0, billing.Value.PaymentTermsDays));
            invoice.Status = invoice.Total == 0 ? InvoiceStatus.Paid : InvoiceStatus.Issued;
            invoice.PaidAt = invoice.Total == 0 ? now : null;
            for (var attempt = 1; ; attempt++)
            {
                invoice.Number = (await db.Invoices.MaxAsync(i => i.Number) ?? 0) + 1;
                try
                {
                    await db.SaveChangesAsync();
                    break;
                }
                catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex) && attempt < 5)
                {
                    // Another invoice took the number between reading the highest and saving; take the next one.
                }
            }
            issued++;
            if (!await TrySendAsync(invoice.BillToEmail, BillingEmails.Invoice(invoice, company.Value, InvoiceLink(baseUri, invoice))))
                notEmailed++;
        }
        return new IssueResult(issued, notEmailed);
    }

    // Only while nothing has been paid on it (there are no refunds). The owner is told if it had been issued.
    public async Task VoidAsync(ClaimsPrincipal user, int invoiceId, string? reason, string baseUri)
    {
        Guard.RequireAdmin(user);
        var why = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (why?.Length > 500)
            throw new AppValidationException("Keep the reason to 500 characters.");
        await using var db = await dbFactory.CreateDbContextAsync();
        var invoice = await LoadInvoice(db.Invoices).FirstOrDefaultAsync(i => i.Id == invoiceId) ?? throw new NotFoundException("Invoice not found.");
        if (invoice.Status is InvoiceStatus.Void)
            throw new AppValidationException("This invoice is already void.");
        if (invoice.Status is InvoiceStatus.Paid || invoice.Payments.Count > 0)
            throw new AppValidationException("Payments have been recorded on this invoice, so it can't be voided.");
        var wasIssued = invoice.Status == InvoiceStatus.Issued;
        invoice.Status = InvoiceStatus.Void;
        invoice.VoidedAt = time.GetUtcNow();
        invoice.VoidReason = why;
        await db.SaveChangesAsync();
        if (wasIssued)
            await TrySendAsync(invoice.BillToEmail, BillingEmails.Voided(invoice, company.Value, InvoiceLink(baseUri, invoice)));
    }

    // Returns whether the receipt was emailed.
    public async Task<bool> RecordPaymentAsync(ClaimsPrincipal user, int invoiceId, PaymentInput input, string baseUri)
    {
        Guard.RequireAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var invoice = await LoadInvoice(db.Invoices).FirstOrDefaultAsync(i => i.Id == invoiceId) ?? throw new NotFoundException("Invoice not found.");
        if (invoice.Status != InvoiceStatus.Issued)
            throw new AppValidationException(invoice.Status switch
            {
                InvoiceStatus.Draft => "Issue the invoice before recording a payment.",
                InvoiceStatus.Paid => "This invoice is already paid.",
                _ => "This invoice is void.",
            });
        if (input.Amount <= 0 || decimal.Round(input.Amount, 2) != input.Amount)
            throw new AppValidationException("Enter the amount received in dollars and cents.");
        if (input.Amount > invoice.Balance)
            throw new AppValidationException($"That's more than the {Money.Format(invoice.Balance)} still owed.");
        // Billing dates are UTC dates; in the US the local date is never ahead of UTC's, so today is the latest real one.
        if (input.ReceivedOn > Today)
            throw new AppValidationException("The date received can't be in the future.");
        var reference = string.IsNullOrWhiteSpace(input.Reference) ? null : input.Reference.Trim();
        if (reference?.Length > 100)
            throw new AppValidationException("Keep the reference to 100 characters.");
        var now = time.GetUtcNow();
        var payment = new InvoicePayment
        {
            Amount = input.Amount, Method = input.Method, Reference = reference, ReceivedOn = input.ReceivedOn,
            RecordedById = Guard.RequireUserId(user), RecordedAt = now,
        };
        invoice.Payments.Add(payment);
        if (invoice.Balance == 0)
        {
            invoice.Status = InvoiceStatus.Paid;
            invoice.PaidAt = now;
        }
        await db.SaveChangesAsync();
        return await TrySendAsync(invoice.BillToEmail, BillingEmails.Receipt(invoice, payment, company.Value, InvoiceLink(baseUri, invoice)));
    }

    // Sends the invoice again, or for a paid one the receipt for its latest payment. Tells the admin if it fails.
    public async Task ResendAsync(ClaimsPrincipal user, int invoiceId, string baseUri)
    {
        Guard.RequireAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var invoice = await LoadInvoice(db.Invoices.AsNoTracking()).FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new NotFoundException("Invoice not found.");
        if (invoice.Status is InvoiceStatus.Draft or InvoiceStatus.Void)
            throw new AppValidationException("Only issued and paid invoices can be sent.");
        if (invoice.BillToEmail is null)
            throw new AppValidationException("This invoice has no email address to send to.");
        var link = InvoiceLink(baseUri, invoice);
        var message = invoice.Status == InvoiceStatus.Paid && invoice.Payments.Count > 0
            ? BillingEmails.Receipt(invoice, invoice.Payments.OrderBy(p => p.RecordedAt).Last(), company.Value, link)
            : BillingEmails.Invoice(invoice, company.Value, link);
        await email.SendOrReportAsync(logger, invoice.BillToEmail, new AccountEmail(message.Subject, message.Html),
            "The email couldn't be sent. Try again in a few minutes.");
    }

    // --- The theater's view (billing.view, or billing.manage for the subscription) ---

    public async Task<TheaterBilling> GetTheaterBillingAsync(ClaimsPrincipal user, int theaterId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.AsNoTracking().Include(t => t.Owner).FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");
        // Managing billing (changing the address, cancelling) needs to see the subscription, but not the invoices.
        var canView = (await auth.AuthorizeAsync(user, theater, new TheaterPermissionRequirement(TheaterPermissions.ViewBilling))).Succeeded;
        if (!canView)
            await auth.RequireAsync(user, theater, TheaterPermissions.ManageBilling);
        var sub = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.TheaterId == theaterId);
        List<Invoice> invoices = !canView ? [] : await db.Invoices.AsNoTracking().Include(i => i.Payments)
            .Where(i => i.TheaterId == theaterId && i.Status != InvoiceStatus.Draft)
            .OrderByDescending(i => i.PeriodMonth).ThenByDescending(i => i.Id)
            .ToListAsync();
        return new TheaterBilling(sub, sub?.BillingEmail ?? theater.Owner?.Email, invoices, Today);
    }

    public async Task<Invoice> GetInvoiceAsync(ClaimsPrincipal user, int theaterId, int invoiceId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");
        await auth.RequireAsync(user, theater, TheaterPermissions.ViewBilling);
        return await LoadInvoice(db.Invoices.AsNoTracking())
            .FirstOrDefaultAsync(i => i.Id == invoiceId && i.TheaterId == theaterId && i.Status != InvoiceStatus.Draft)
            ?? throw new NotFoundException("Invoice not found.");
    }

    // --- Helpers ---

    public static string InvoiceLink(string baseUri, Invoice invoice) =>
        invoice.TheaterId is int theaterId ? $"{baseUri.TrimEnd('/')}/manage/{theaterId}/billing/invoices/{invoice.Id}" : baseUri;

    private static IQueryable<Invoice> LoadInvoice(IQueryable<Invoice> invoices) =>
        invoices.Include(i => i.Lines.OrderBy(l => l.SortOrder)).Include(i => i.Payments.OrderBy(p => p.ReceivedOn));

    private static async Task<Invoice> RequireDraftAsync(ApplicationDbContext db, int invoiceId)
    {
        var invoice = await LoadInvoice(db.Invoices).FirstOrDefaultAsync(i => i.Id == invoiceId) ?? throw new NotFoundException("Invoice not found.");
        if (invoice.Status != InvoiceStatus.Draft)
            throw new AppValidationException($"{invoice.DisplayNumber} has been issued, so it can't be changed. Void it and draft another instead.");
        return invoice;
    }

    private static async Task<Subscription> RequireSubscriptionAsync(ApplicationDbContext db, int theaterId) =>
        await db.Subscriptions.FirstOrDefaultAsync(s => s.TheaterId == theaterId)
        ?? throw new AppValidationException("This theater has no subscription.");

    private static decimal RequirePrice(decimal price) =>
        price is > 0 and <= MaxLineAmount && decimal.Round(price, 2) == price
            ? price
            : throw new AppValidationException($"The price per screen must be more than $0 and at most {Money.Format(MaxLineAmount)}.");

    private static string E(string? text) => System.Net.WebUtility.HtmlEncode(text ?? "");

    private Task<bool> TrySendAsync(string? to, BillingEmail message) => TrySendAsync(to, message.Subject, message.Html);

    private async Task<bool> TrySendAsync(string? to, string subject, string html)
    {
        if (string.IsNullOrWhiteSpace(to))
            return false;
        try
        {
            await email.SendAsync(to, subject, html);
            return true;
        }
        catch (Exception ex)
        {
            // The recipient isn't logged: email addresses don't belong in production logs.
            logger.LogError(ex, "Couldn't send email {Subject}", subject);
            return false;
        }
    }
}

// Drafts invoices as months come due: once at startup, then hourly.
public sealed class BillingJobService(IServiceScopeFactory scopes, TimeProvider time, ILogger<BillingJobService> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<BillingService>().RunScheduledAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Couldn't draft invoices");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
