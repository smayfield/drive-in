using System.Security.Claims;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;

namespace DriveIn.Web.Tests;

public class BillingTests
{
    // TestApp's clock starts at 2026-09-01 12:00 UTC (7 AM in Chicago); the plan price is $49 per screen.
    private static readonly DateOnly Sep = new(2026, 9, 1);
    private static readonly DateOnly Oct = new(2026, 10, 1);

    private sealed record Setup(TestApp App, ApplicationUser Owner, ClaimsPrincipal OwnerUser, ClaimsPrincipal Admin, Theater Theater)
    {
        public BillingService Billing => App.Get<BillingService>();
    }

    // A self-signed-up theater with two screens, activated by an admin.
    private static async Task<Setup> LiveTheaterAsync(TestApp? app = null, string name = "Starlight", string ownerEmail = "owner@example.com")
    {
        app ??= new TestApp();
        var owner = await app.CreateUserAsync(ownerEmail);
        ApplicationUser? adminUser;
        await using (var db = app.Db())
            adminUser = await db.Users.FirstOrDefaultAsync(u => u.Email == "admin@example.com");
        adminUser ??= await app.CreateUserAsync("admin@example.com", admin: true);
        var admin = Principals.For(adminUser, admin: true);
        var onboarding = app.Get<OnboardingService>();
        var theater = await onboarding.CreateDemoTheaterAsync(Principals.For(owner),
            new NewTheaterInput(name, "Austin", "TX", "America/Chicago", 2, true));
        await onboarding.ActivateAsync(admin, theater.Id, TestApp.BaseUri);
        return new Setup(app, owner, Principals.For(owner), admin, theater);
    }

    private static async Task<Invoice> OnlyDraftAsync(TestApp app, int theaterId)
    {
        await using var db = app.Db();
        return await db.Invoices.Include(i => i.Lines).SingleAsync(i => i.TheaterId == theaterId && i.Status == InvoiceStatus.Draft);
    }

    private static async Task SetSeasonAsync(TestApp app, int theaterId, DateOnly? opens, DateOnly? closes)
    {
        await using var db = app.Db();
        var theater = await db.Theaters.SingleAsync(t => t.Id == theaterId);
        (theater.SeasonOpensOn, theater.SeasonClosesOn) = (opens, closes);
        await db.SaveChangesAsync();
    }

    // --- Rules ---

    [Fact]
    public void A_month_is_billed_when_the_subscription_runs_and_the_season_touches_it()
    {
        var theater = new Theater { Mode = TheaterMode.Live, Screens = [new Screen()] };
        var sub = new Subscription { StartedOn = new DateOnly(2026, 5, 20) };

        Assert.True(BillingService.IsBillable(sub, theater, new DateOnly(2026, 5, 1))); // started mid-month: the whole month
        Assert.False(BillingService.IsBillable(sub, theater, new DateOnly(2026, 4, 1)));
        Assert.True(BillingService.IsBillable(sub, theater, new DateOnly(2027, 1, 1))); // no season: every month

        theater.SeasonOpensOn = new DateOnly(2026, 5, 31);
        theater.SeasonClosesOn = new DateOnly(2026, 9, 2);
        Assert.True(BillingService.IsBillable(sub, theater, new DateOnly(2026, 5, 1))); // open one day of May
        Assert.True(BillingService.IsBillable(sub, theater, Sep));
        Assert.False(BillingService.IsBillable(sub, theater, Oct));

        sub.EndsAfterMonth = new DateOnly(2026, 6, 1);
        Assert.True(BillingService.IsBillable(sub, theater, new DateOnly(2026, 6, 1)));
        Assert.False(BillingService.IsBillable(sub, theater, new DateOnly(2026, 7, 1)));

        sub.EndsAfterMonth = null;
        theater.IsActive = false;
        Assert.False(BillingService.IsBillable(sub, theater, Sep));
        theater.IsActive = true;
        theater.Screens.Clear();
        Assert.False(BillingService.IsBillable(sub, theater, Sep));
    }

    // --- Subscriptions and drafts ---

    [Fact]
    public async Task Going_live_starts_the_subscription_and_drafts_the_whole_first_month()
    {
        var s = await LiveTheaterAsync();
        await using var _ = s.App;

        await using var db = s.App.Db();
        var sub = await db.Subscriptions.SingleAsync(x => x.TheaterId == s.Theater.Id);
        Assert.Equal((49m, SubscriptionStatus.Active, Sep), (sub.PricePerScreenPerMonth, sub.Status, sub.StartedOn));
        var draft = await OnlyDraftAsync(s.App, s.Theater.Id);
        Assert.Equal((Sep, 98m, "owner@example.com", "Starlight"), (draft.PeriodMonth, draft.Total, draft.BillToEmail, draft.TheaterName));
        var line = Assert.Single(draft.Lines);
        Assert.Equal((2, 49m, "Standard plan, September 2026: 2 screens"), (line.Quantity, line.UnitPrice, line.Description));
        Assert.Null(draft.Number);
        Assert.DoesNotContain(s.App.Email.Sent, m => m.Subject.Contains("Invoice"));

        // The owner doesn't see drafts.
        var billing = await s.Billing.GetTheaterBillingAsync(s.OwnerUser, s.Theater.Id);
        Assert.NotNull(billing.Subscription);
        Assert.Empty(billing.Invoices);
    }

    [Fact]
    public async Task Drafting_is_idempotent_and_catches_up_a_missed_month()
    {
        var s = await LiveTheaterAsync();
        await using var _ = s.App;

        Assert.Equal(0, await s.Billing.GenerateDraftsAsync(s.Admin));

        // Nothing ran in October; on November 2 both October and November are drafted, once.
        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(2, await s.Billing.RunScheduledAsync());
        Assert.Equal(0, await s.Billing.RunScheduledAsync());
        await using var db = s.App.Db();
        Assert.Equal([Sep, Oct, new DateOnly(2026, 11, 1)],
            await db.Invoices.Where(i => i.TheaterId == s.Theater.Id).OrderBy(i => i.PeriodMonth).Select(i => i.PeriodMonth).ToListAsync());
        // The admins hear there are drafts to review.
        Assert.Contains(s.App.Email.Sent, m => m.To == "admin@example.com" && m.Subject == "2 draft invoices to review");
    }

    [Fact]
    public async Task Months_outside_the_season_are_not_drafted()
    {
        var s = await LiveTheaterAsync();
        await using var _ = s.App;
        await SetSeasonAsync(s.App, s.Theater.Id, new DateOnly(2027, 4, 15), new DateOnly(2027, 9, 30));

        s.App.Time.SetUtcNow(new DateTimeOffset(2027, 1, 5, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(0, await s.Billing.GenerateDraftsAsync(s.Admin));
        s.App.Time.SetUtcNow(new DateTimeOffset(2027, 4, 1, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(1, await s.Billing.GenerateDraftsAsync(s.Admin)); // April, the month it opens
    }

    [Fact]
    public async Task A_price_change_applies_to_new_drafts_only()
    {
        var s = await LiveTheaterAsync();
        await using var _ = s.App;

        await s.Billing.ChangePriceAsync(s.Admin, s.Theater.Id, 59m);
        await Assert.ThrowsAsync<AppValidationException>(() => s.Billing.ChangePriceAsync(s.Admin, s.Theater.Id, 0m));
        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        await s.Billing.GenerateDraftsAsync(s.Admin);

        await using var db = s.App.Db();
        Assert.Equal([98m, 118m],
            await db.Invoices.Where(i => i.TheaterId == s.Theater.Id).OrderBy(i => i.PeriodMonth).Select(i => i.Total).ToListAsync());
    }

    [Fact]
    public async Task Live_theaters_without_a_subscription_can_be_started_by_an_admin()
    {
        await using var app = new TestApp();
        var admin = Principals.For(await app.CreateUserAsync("admin@example.com", admin: true), admin: true);
        var theater = await app.Get<TheaterService>().CreateAsync(admin, new Theater { Name = "Moonlight", Slug = "moonlight" });
        var billing = app.Get<BillingService>();

        var row = Assert.Single(await billing.ListSubscriptionsAsync(admin));
        Assert.Equal((theater.Id, (Subscription?)null), (row.TheaterId, row.Subscription));

        await billing.StartSubscriptionAsync(admin, theater.Id, 39m);
        await Assert.ThrowsAsync<AppValidationException>(() => billing.StartSubscriptionAsync(admin, theater.Id, null));
        row = Assert.Single(await billing.ListSubscriptionsAsync(admin));
        Assert.Equal((39m, 39m), (row.Subscription!.PricePerScreenPerMonth, row.MonthlyTotal)); // one screen
        Assert.Equal(39m, (await OnlyDraftAsync(app, theater.Id)).Total);
    }

    // --- Draft lines and issuing ---

    [Fact]
    public async Task Admins_adjust_drafts_and_issuing_numbers_and_emails_them()
    {
        var s = await LiveTheaterAsync();
        await using var _ = s.App;
        var second = await LiveTheaterAsync(s.App, "Moonlight", "moon@example.com");
        var draft = await OnlyDraftAsync(s.App, s.Theater.Id);

        await s.Billing.AddLineAsync(s.Admin, draft.Id, "Opening-month credit", 1, -10m);
        await Assert.ThrowsAsync<AppValidationException>(() => s.Billing.AddLineAsync(s.Admin, draft.Id, "", 1, 5m));
        await Assert.ThrowsAsync<AppValidationException>(() => s.Billing.AddLineAsync(s.Admin, draft.Id, "Odd", 1, 0.001m));
        Assert.Equal(88m, (await s.Billing.GetInvoiceForAdminAsync(s.Admin, draft.Id)).Total);

        // A voided draft doesn't use up a number.
        var voided = await OnlyDraftAsync(s.App, second.Theater.Id);
        await s.Billing.VoidAsync(s.Admin, voided.Id, "Comped first month", TestApp.BaseUri);
        await s.Billing.UpdateBillingEmailAsync(s.OwnerUser, s.Theater.Id, "accounts@example.com");

        var result = await s.Billing.IssueAllDraftsAsync(s.Admin, TestApp.BaseUri);

        Assert.Equal(new IssueResult(1, 0), result);
        var issued = await s.Billing.GetInvoiceForAdminAsync(s.Admin, draft.Id);
        Assert.Equal(("INV-000001", InvoiceStatus.Issued, new DateOnly(2026, 9, 16)), (issued.DisplayNumber, issued.Status, issued.DueOn));
        var sent = Assert.Single(s.App.Email.Sent, m => m.Subject.StartsWith("Invoice INV-000001"));
        Assert.Equal("accounts@example.com", sent.To);
        Assert.Contains("Opening-month credit", sent.Body);
        Assert.Contains("$88.00", sent.Body);
        Assert.Contains($"manage/{s.Theater.Id}/billing/invoices/{draft.Id}", sent.Body);
        // Nobody was told about the voided draft: it was never issued.
        Assert.DoesNotContain(s.App.Email.Sent, m => m.Subject.Contains("void"));

        // Issued invoices can't be edited, and the next one gets the next number.
        await Assert.ThrowsAsync<AppValidationException>(() => s.Billing.AddLineAsync(s.Admin, draft.Id, "More", 1, 5m));
        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        await s.Billing.GenerateDraftsAsync(s.Admin);
        var october = await OnlyDraftAsync(s.App, s.Theater.Id);
        await s.Billing.IssueAsync(s.Admin, [october.Id], TestApp.BaseUri);
        Assert.Equal(2, (await s.Billing.GetInvoiceForAdminAsync(s.Admin, october.Id)).Number);
    }

    [Fact]
    public async Task An_invoice_below_zero_cant_be_issued_and_a_zero_one_is_issued_paid()
    {
        var s = await LiveTheaterAsync();
        await using var _ = s.App;
        var draft = await OnlyDraftAsync(s.App, s.Theater.Id);

        await s.Billing.AddLineAsync(s.Admin, draft.Id, "Credit", 1, -100m);
        await Assert.ThrowsAsync<AppValidationException>(() => s.Billing.IssueAsync(s.Admin, [draft.Id], TestApp.BaseUri));

        var credit = (await s.Billing.GetInvoiceForAdminAsync(s.Admin, draft.Id)).Lines.Single(l => l.Amount < 0);
        await s.Billing.RemoveLineAsync(s.Admin, draft.Id, credit.Id);
        await s.Billing.AddLineAsync(s.Admin, draft.Id, "Free first month", 1, -98m);
        await s.Billing.IssueAsync(s.Admin, [draft.Id], TestApp.BaseUri);

        var invoice = await s.Billing.GetInvoiceForAdminAsync(s.Admin, draft.Id);
        Assert.Equal((InvoiceStatus.Paid, 0m), (invoice.Status, invoice.Total));
        Assert.NotNull(invoice.PaidAt);
    }

    // --- Payments and voiding ---

    [Fact]
    public async Task Payments_are_recorded_with_a_receipt_each_until_paid()
    {
        var s = await LiveTheaterAsync();
        await using var _ = s.App;
        var draft = await OnlyDraftAsync(s.App, s.Theater.Id);
        await Assert.ThrowsAsync<AppValidationException>(() =>
            s.Billing.RecordPaymentAsync(s.Admin, draft.Id, new PaymentInput(10m, PaymentMethod.Check, null, Sep), TestApp.BaseUri));
        await s.Billing.IssueAsync(s.Admin, [draft.Id], TestApp.BaseUri);

        Assert.True(await s.Billing.RecordPaymentAsync(s.Admin, draft.Id, new PaymentInput(50m, PaymentMethod.Check, "1042", Sep), TestApp.BaseUri));
        var partial = await s.Billing.GetInvoiceForAdminAsync(s.Admin, draft.Id);
        Assert.Equal((InvoiceStatus.Issued, 48m), (partial.Status, partial.Balance));
        var receipt = Assert.Single(s.App.Email.Sent, m => m.Subject.StartsWith("Receipt"));
        Assert.Contains("$48.00 is still due", receipt.Body);
        Assert.Contains("Check (1042)", receipt.Body);

        await Assert.ThrowsAsync<AppValidationException>(() =>
            s.Billing.RecordPaymentAsync(s.Admin, draft.Id, new PaymentInput(49m, PaymentMethod.Check, null, Sep), TestApp.BaseUri));
        await Assert.ThrowsAsync<AppValidationException>(() =>
            s.Billing.RecordPaymentAsync(s.Admin, draft.Id, new PaymentInput(1m, PaymentMethod.Check, null, Sep.AddDays(1)), TestApp.BaseUri)); // tomorrow
        await Assert.ThrowsAsync<AppValidationException>(() => s.Billing.VoidAsync(s.Admin, draft.Id, null, TestApp.BaseUri));

        await s.Billing.RecordPaymentAsync(s.Admin, draft.Id, new PaymentInput(48m, PaymentMethod.BankTransfer, null, Sep), TestApp.BaseUri);
        var paid = await s.Billing.GetInvoiceForAdminAsync(s.Admin, draft.Id);
        Assert.Equal((InvoiceStatus.Paid, 0m, 2), (paid.Status, paid.Balance, paid.Payments.Count));
        Assert.Equal(2, s.App.Email.Sent.Count(m => m.Subject.StartsWith("Receipt")));
        Assert.Contains("paid in full", s.App.Email.Sent.Last().Body);
        await Assert.ThrowsAsync<AppValidationException>(() =>
            s.Billing.RecordPaymentAsync(s.Admin, draft.Id, new PaymentInput(1m, PaymentMethod.Check, null, Sep), TestApp.BaseUri));

        // Resending a paid invoice sends the receipt again.
        await s.Billing.ResendAsync(s.Admin, draft.Id, TestApp.BaseUri);
        Assert.Equal(3, s.App.Email.Sent.Count(m => m.Subject.StartsWith("Receipt")));
    }

    [Fact]
    public async Task Voiding_an_issued_invoice_tells_the_owner_and_lets_the_month_be_drafted_again()
    {
        var s = await LiveTheaterAsync();
        await using var _ = s.App;
        var draft = await OnlyDraftAsync(s.App, s.Theater.Id);
        await s.Billing.IssueAsync(s.Admin, [draft.Id], TestApp.BaseUri);

        await s.Billing.VoidAsync(s.Admin, draft.Id, "Wrong screen count", TestApp.BaseUri);

        var notice = Assert.Single(s.App.Email.Sent, m => m.Subject.EndsWith("is void"));
        Assert.Equal("owner@example.com", notice.To);
        Assert.Contains("Wrong screen count", notice.Body);
        Assert.Contains($"manage/{s.Theater.Id}/billing/invoices/{draft.Id}", notice.Body);
        Assert.Equal(1, await s.Billing.GenerateDraftsAsync(s.Admin));
    }

    [Fact]
    public async Task A_failed_invoice_email_still_issues_it_and_is_counted()
    {
        var s = await LiveTheaterAsync();
        await using var _ = s.App;
        var draft = await OnlyDraftAsync(s.App, s.Theater.Id);
        s.App.Email.FailWith = new EmailSendException("SES is down");

        Assert.Equal(new IssueResult(1, 1), await s.Billing.IssueAsync(s.Admin, [draft.Id], TestApp.BaseUri));
        Assert.Equal(InvoiceStatus.Issued, (await s.Billing.GetInvoiceForAdminAsync(s.Admin, draft.Id)).Status);
        await Assert.ThrowsAsync<AppValidationException>(() => s.Billing.ResendAsync(s.Admin, draft.Id, TestApp.BaseUri));
    }

    // --- Cancelling ---

    [Fact]
    public async Task Cancelling_stops_billing_after_this_month_and_reactivating_restarts_from_the_current_month()
    {
        var s = await LiveTheaterAsync();
        await using var _ = s.App;

        await s.Billing.CancelAsync(s.OwnerUser, s.Theater.Id);
        await Assert.ThrowsAsync<AppValidationException>(() => s.Billing.CancelAsync(s.OwnerUser, s.Theater.Id));
        var sub = (await s.Billing.GetTheaterBillingAsync(s.OwnerUser, s.Theater.Id)).Subscription!;
        Assert.Equal((SubscriptionStatus.Canceled, Sep, s.Owner.Id), (sub.Status, sub.EndsAfterMonth, sub.CanceledById));
        Assert.Contains(s.App.Email.Sent, m => m.To == "owner@example.com" && m.Subject.StartsWith("Subscription cancelled"));
        Assert.Contains(s.App.Email.Sent, m => m.To == "admin@example.com" && m.Subject.EndsWith("cancelled its subscription"));

        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(0, await s.Billing.GenerateDraftsAsync(s.Admin));

        // Back in December: October and November stay unbilled.
        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 12, 3, 12, 0, 0, TimeSpan.Zero));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Billing.ReactivateAsync(s.OwnerUser, s.Theater.Id));
        await s.Billing.ReactivateAsync(s.Admin, s.Theater.Id);
        await using var db = s.App.Db();
        Assert.Equal([Sep, new DateOnly(2026, 12, 1)],
            await db.Invoices.Where(i => i.TheaterId == s.Theater.Id).OrderBy(i => i.PeriodMonth).Select(i => i.PeriodMonth).ToListAsync());
    }

    // --- Authorization ---

    [Fact]
    public async Task Admin_billing_work_is_for_admins_only()
    {
        var s = await LiveTheaterAsync();
        await using var _ = s.App;
        var draft = await OnlyDraftAsync(s.App, s.Theater.Id);

        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Billing.IssueAsync(s.OwnerUser, [draft.Id], TestApp.BaseUri));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Billing.ListInvoicesAsync(s.OwnerUser, new InvoiceFilter()));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Billing.GetInvoiceForAdminAsync(s.OwnerUser, draft.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Billing.ChangePriceAsync(s.OwnerUser, s.Theater.Id, 1m));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Billing.GenerateDraftsAsync(s.OwnerUser));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            s.Billing.RecordPaymentAsync(s.OwnerUser, draft.Id, new PaymentInput(1m, PaymentMethod.Check, null, Sep), TestApp.BaseUri));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.App.Get<BillingReportService>().GetReportAsync(s.OwnerUser, Sep, Sep));
    }

    [Fact]
    public async Task Employees_need_billing_permissions_which_no_default_role_has()
    {
        var s = await LiveTheaterAsync();
        await using var _ = s.App;
        var draft = await OnlyDraftAsync(s.App, s.Theater.Id);
        await s.Billing.IssueAsync(s.Admin, [draft.Id], TestApp.BaseUri);
        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        await s.Billing.GenerateDraftsAsync(s.Admin); // an October draft

        // A Manager (the default role with everything else) still can't see billing.
        var manager = await s.App.CreateUserAsync("manager@example.com", s.Theater.Id);
        await using (var db = s.App.Db())
        {
            var role = await db.TheaterRoles.SingleAsync(r => r.TheaterId == s.Theater.Id && r.Name == "Manager");
            db.EmployeeRoles.Add(new EmployeeRole { UserId = manager.Id, RoleId = role.Id });
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Billing.GetTheaterBillingAsync(Principals.For(manager), s.Theater.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Billing.GetInvoiceAsync(Principals.For(manager), s.Theater.Id, draft.Id));

        var bookkeeper = await s.App.CreateUserAsync("books@example.com", s.Theater.Id);
        await s.App.GrantAsync(bookkeeper, ViewBilling);
        var billing = await s.Billing.GetTheaterBillingAsync(Principals.For(bookkeeper), s.Theater.Id);
        Assert.Equal(["INV-000001"], billing.Invoices.Select(i => i.DisplayNumber)); // not the October draft
        Assert.Equal(98m, (await s.Billing.GetInvoiceAsync(Principals.For(bookkeeper), s.Theater.Id, draft.Id)).Total);
        var october = await OnlyDraftAsync(s.App, s.Theater.Id);
        await Assert.ThrowsAsync<NotFoundException>(() => s.Billing.GetInvoiceAsync(Principals.For(bookkeeper), s.Theater.Id, october.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Billing.CancelAsync(Principals.For(bookkeeper), s.Theater.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Billing.UpdateBillingEmailAsync(Principals.For(bookkeeper), s.Theater.Id, "x@example.com"));

        // Another theater's owner sees nothing here.
        var other = await LiveTheaterAsync(s.App, "Moonlight", "moon@example.com");
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Billing.GetTheaterBillingAsync(other.OwnerUser, s.Theater.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => s.Billing.GetInvoiceAsync(other.OwnerUser, other.Theater.Id, draft.Id));

        var treasurer = await s.App.CreateUserAsync("treasurer@example.com", s.Theater.Id);
        await s.App.GrantAsync(treasurer, ManageBilling);
        await Assert.ThrowsAsync<AppValidationException>(() => s.Billing.UpdateBillingEmailAsync(Principals.For(treasurer), s.Theater.Id, "not an email"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => s.Billing.GetInvoiceAsync(Principals.For(treasurer), s.Theater.Id, draft.Id));
        var treasurerView = await s.Billing.GetTheaterBillingAsync(Principals.For(treasurer), s.Theater.Id);
        Assert.NotNull(treasurerView.Subscription);
        Assert.Empty(treasurerView.Invoices); // that needs billing.view
        await s.Billing.CancelAsync(Principals.For(treasurer), s.Theater.Id);
    }

    // --- Deleting a theater ---

    [Fact]
    public async Task Deleting_a_theater_keeps_its_issued_invoices()
    {
        var s = await LiveTheaterAsync();
        await using var _ = s.App;
        var draft = await OnlyDraftAsync(s.App, s.Theater.Id);
        await s.Billing.IssueAsync(s.Admin, [draft.Id], TestApp.BaseUri);
        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        await s.Billing.GenerateDraftsAsync(s.Admin);

        await s.App.Get<TheaterService>().DeleteAsync(s.Admin, s.Theater.Id);

        var left = Assert.Single(await s.Billing.ListInvoicesAsync(s.Admin, new InvoiceFilter()));
        Assert.Equal(("INV-000001", "Starlight", (int?)null), (left.DisplayNumber, left.TheaterName, left.TheaterId));
        await using var db = s.App.Db();
        Assert.False(await db.Subscriptions.AnyAsync());
    }

    // --- Reports ---

    [Fact]
    public async Task The_report_totals_months_ages_receivables_and_projects_revenue()
    {
        var s = await LiveTheaterAsync();
        await using var _ = s.App;
        var second = await LiveTheaterAsync(s.App, "Moonlight", "moon@example.com");
        await SetSeasonAsync(s.App, second.Theater.Id, new DateOnly(2026, 5, 1), new DateOnly(2026, 9, 30));

        var september = await s.Billing.IssueAllDraftsAsync(s.Admin, TestApp.BaseUri); // both theaters, due Sep 16
        Assert.Equal(2, september.Issued);
        var first = (await s.Billing.ListInvoicesAsync(s.Admin, new InvoiceFilter(TheaterId: s.Theater.Id))).Single();
        await s.Billing.RecordPaymentAsync(s.Admin, first.Id, new PaymentInput(98m, PaymentMethod.Check, null, Sep), TestApp.BaseUri);

        // October: Starlight (no season) is billed again; Moonlight's season has closed. The Moonlight invoice is 35 days late.
        s.App.Time.SetUtcNow(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        await s.Billing.GenerateDraftsAsync(s.Admin);
        await s.Billing.IssueAllDraftsAsync(s.Admin, TestApp.BaseUri);

        var report = await s.App.Get<BillingReportService>().GetReportAsync(s.Admin, Sep, Oct.AddDays(20));

        Assert.Equal((2, 0, 4), (report.ActiveSubscriptions, report.CanceledSubscriptions, report.BilledScreens));
        Assert.Equal((98m, 98m), (report.ProjectedThisMonth, report.ProjectedNextMonth));
        Assert.Equal([(Sep, 2, 196m, 98m), (Oct, 1, 98m, 0m)], report.Months.Select(m => (m.Month, m.Invoices, m.Invoiced, m.Collected)));
        Assert.Equal([("Moonlight", 98m, 0m, 98m), ("Starlight", 196m, 98m, 98m)],
            report.Theaters.Select(t => (t.TheaterName, t.Invoiced, t.Paid, t.Outstanding)));
        Assert.Equal((98m, 0m, 98m, 196m), (report.AgingTotal.Current, report.AgingTotal.Days1To30, report.AgingTotal.Days31To60, report.AgingTotal.Total));
        Assert.Single(report.Payments);

        var aging = ReportCsv.BillingAging(report);
        Assert.StartsWith("Theater,Current,1-30 days", aging);
        Assert.Contains("Total,98.00,0.00,98.00,0.00,0.00,196.00", aging);
        Assert.Contains("INV-000001,Starlight,2026-09-01,2026-09-01,2026-09-16,Paid,98.00,98.00,0.00", ReportCsv.BillingInvoices(report));

        await Assert.ThrowsAsync<AppValidationException>(() => s.App.Get<BillingReportService>().GetReportAsync(s.Admin, Oct, Sep));
    }
}
