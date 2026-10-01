using System.Diagnostics.Metrics;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static DriveIn.Web.Tests.TicketSalesTests;

namespace DriveIn.Web.Tests;

public class MetricsTests
{
    private static MetricCollector<T> Collect<T>(TestApp app, string instrument) where T : struct =>
        new(app.Get<IMeterFactory>(), DriveInMetrics.MeterName, instrument);

    private static object? Tag(CollectedMeasurement<long> m, string name) => m.Tags.GetValueOrDefault(name);

    [Fact]
    public async Task An_online_sale_counts_the_ticket_its_revenue_and_the_approved_charge()
    {
        await using var s = await SetUpAsync();
        using var sold = Collect<long>(s.App, "drivein.tickets.sold");
        using var revenue = Collect<double>(s.App, "drivein.tickets.revenue");
        using var payments = Collect<long>(s.App, "drivein.payments");
        var buyer = await BuyerAsync(s.App);

        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);
        await s.Sales.PurchaseAsync(buyer, hold.TicketId, Buy(s.CarLoad, s.OutsideFood), TestApp.BaseUri);

        var ticket = Assert.Single(sold.GetMeasurementSnapshot());
        Assert.Equal(1, ticket.Value);
        Assert.Equal(DriveInMetrics.Online, Tag(ticket, "channel"));
        Assert.Equal(false, Tag(ticket, "test"));
        Assert.Equal(30d, Assert.Single(revenue.GetMeasurementSnapshot()).Value);
        var charge = Assert.Single(payments.GetMeasurementSnapshot());
        Assert.Equal("ticket", Tag(charge, "for"));
        Assert.Equal("approved", Tag(charge, "result"));
    }

    [Fact]
    public async Task A_declined_card_counts_the_decline_and_no_sale()
    {
        await using var s = await SetUpAsync();
        using var sold = Collect<long>(s.App, "drivein.tickets.sold");
        using var payments = Collect<long>(s.App, "drivein.payments");
        var buyer = await BuyerAsync(s.App);
        s.App.Payments.DeclineWith = "insufficient funds";

        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);
        await Assert.ThrowsAsync<AppValidationException>(() => s.Sales.PurchaseAsync(buyer, hold.TicketId, Buy(s.Single), TestApp.BaseUri));

        Assert.Empty(sold.GetMeasurementSnapshot());
        Assert.Equal("declined", Tag(Assert.Single(payments.GetMeasurementSnapshot()), "result"));
    }

    [Fact]
    public async Task A_processor_failure_counts_as_a_payment_error_and_the_buyer_keeps_the_hold()
    {
        await using var s = await SetUpAsync();
        using var payments = Collect<long>(s.App, "drivein.payments");
        var buyer = await BuyerAsync(s.App);
        s.App.Payments.FailWith = new HttpRequestException("processor unreachable");

        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);
        await Assert.ThrowsAsync<HttpRequestException>(() => s.Sales.PurchaseAsync(buyer, hold.TicketId, Buy(s.Single), TestApp.BaseUri));

        Assert.Equal("error", Tag(Assert.Single(payments.GetMeasurementSnapshot()), "result"));
        // Back to held (not stuck paying), so the buyer can try again.
        await using var db = s.App.Db();
        Assert.Equal(TicketStatus.Held, (await db.Tickets.FindAsync(hold.TicketId))!.Status);
    }

    [Fact]
    public async Task A_gate_sale_counts_as_sold_and_admitted_at_the_gate()
    {
        await using var s = await SetUpAsync();
        using var sold = Collect<long>(s.App, "drivein.tickets.sold");
        using var admitted = Collect<long>(s.App, "drivein.tickets.admitted");
        s.App.Time.SetUtcNow(s.Showing.StartsAt.AddMinutes(-30));

        var hold = await s.Sales.HoldAtGateAsync(s.OwnerPrincipal, s.Showing.Id, 1, 1);
        await s.Sales.SellAtGateAsync(s.OwnerPrincipal, hold.TicketId, s.Single.Id, []);

        Assert.Equal(DriveInMetrics.Gate, Tag(Assert.Single(sold.GetMeasurementSnapshot()), "channel"));
        Assert.Equal("sold_at_gate", Tag(Assert.Single(admitted.GetMeasurementSnapshot()), "how"));
    }

    [Fact]
    public async Task Checking_a_ticket_in_counts_an_admission()
    {
        await using var s = await SetUpAsync();
        using var admitted = Collect<long>(s.App, "drivein.tickets.admitted");
        var buyer = await BuyerAsync(s.App);
        var hold = await s.Sales.HoldAsync(buyer, s.Showing.Id, 1, 1);
        var bought = await s.Sales.PurchaseAsync(buyer, hold.TicketId, Buy(s.Single), TestApp.BaseUri);
        s.App.Time.SetUtcNow(s.Showing.StartsAt.AddMinutes(-30));

        await s.Sales.AdmitAsync(s.OwnerPrincipal, bought.Code);

        Assert.Equal("scan", Tag(Assert.Single(admitted.GetMeasurementSnapshot()), "how"));
    }

    [Fact]
    public async Task Expired_holds_are_counted_when_released()
    {
        await using var s = await SetUpAsync();
        using var expired = Collect<long>(s.App, "drivein.holds.expired");
        await s.Sales.HoldAsync(await BuyerAsync(s.App, "a@example.com"), s.Showing.Id, 1, 1);
        await s.Sales.HoldAsync(await BuyerAsync(s.App, "b@example.com"), s.Showing.Id, 1, 2);
        s.App.Time.Advance(TimeSpan.FromMinutes(Ticket.HoldMinutes + 1));

        await s.Sales.ReleaseExpiredHoldsAsync();

        Assert.Equal(2, Assert.Single(expired.GetMeasurementSnapshot()).Value);
    }

    [Fact]
    public async Task An_account_an_admin_creates_counts_as_registered_by_admin()
    {
        await using var app = new TestApp();
        using var registered = Collect<long>(app, "drivein.users.registered");
        var admin = await app.CreateUserAsync("admin@example.com", admin: true);

        await app.Get<UserAdminService>().CreateAsync(Principals.For(admin, admin: true), "new@example.com", null, false, TestApp.BaseUri);

        Assert.Equal("admin", Tag(Assert.Single(registered.GetMeasurementSnapshot()), "method"));
    }

    [Fact]
    public async Task Emails_are_counted_as_sent_or_failed()
    {
        await using var app = new TestApp();
        using var emails = Collect<long>(app, "drivein.emails");
        var sender = new MeteredEmailSender(app.Email, app.Get<DriveInMetrics>());

        await sender.SendAsync("a@example.com", "Hi", "<p>Hi</p>");
        app.Email.FailWith = new EmailSendException("rejected");
        await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync("b@example.com", "Hi", "<p>Hi</p>"));

        Assert.Equal(["sent", "failed"], emails.GetMeasurementSnapshot().Select(m => Tag(m, "result")));
    }

    [Fact]
    public async Task Errors_logged_anywhere_are_counted_by_category_and_nothing_milder()
    {
        await using var app = new TestApp();
        using var errors = Collect<long>(app, "drivein.errors.logged");
        using var provider = new ErrorCountingLoggerProvider(app.Get<DriveInMetrics>());
        var logger = provider.CreateLogger("DriveIn.Web.Services.BillingService");

        logger.LogWarning("Just a warning");
        logger.LogError(new InvalidOperationException("boom"), "Couldn't draft invoices");
        logger.LogCritical("Down");

        Assert.Equal([("DriveIn.Web.Services.BillingService", "error"), ("DriveIn.Web.Services.BillingService", "critical")],
            errors.GetMeasurementSnapshot().Select(m => ((string)m.Tags["category"]!, (string)m.Tags["level"]!)));
    }

    [Fact]
    public async Task Business_gauges_report_the_latest_totals_and_nothing_before_the_first_read()
    {
        await using var s = await SetUpAsync();
        var app = s.App;
        await app.CreateUserAsync("employee@example.com", employeeTheaterId: s.Theater.Id);
        var demo = await app.CreateTheaterAsync("Demo Drive-In", s.Owner.Id);
        await using (var db = app.Db())
        {
            var d = await db.Theaters.FindAsync(demo.Id);
            d!.Mode = TheaterMode.Demo;
            d.GoLiveRequestedAt = app.Time.GetUtcNow();
            // A live theater with a leftover request isn't waiting (Admin → Theaters doesn't list it either).
            (await db.Theaters.FindAsync(s.Theater.Id))!.GoLiveRequestedAt = app.Time.GetUtcNow();
            db.Invoices.Add(new Invoice { TheaterName = "Starlight", Status = InvoiceStatus.Issued, Total = 98m, CreatedAt = app.Time.GetUtcNow(),
                Payments = [new InvoicePayment { Amount = 40m, ReceivedOn = new DateOnly(2026, 9, 1), RecordedAt = app.Time.GetUtcNow() }] });
            db.Invoices.Add(new Invoice { TheaterName = "Starlight", Status = InvoiceStatus.Draft, Total = 49m, CreatedAt = app.Time.GetUtcNow() });
            await db.SaveChangesAsync();
        }
        using var users = Collect<double>(app, "drivein.users");
        using var owed = Collect<double>(app, "drivein.invoices.outstanding");
        var gauges = new BusinessGauges(app.Services.GetRequiredService<IServiceScopeFactory>(), app.Time,
            app.Get<IMeterFactory>(), app.Get<DriveInMetrics>(), NullLogger<BusinessGauges>.Instance);

        users.RecordObservableInstruments();
        Assert.Empty(users.GetMeasurementSnapshot());

        var snapshot = await gauges.RefreshAsync();

        // The owner, the employee (Setup has no other accounts); Starlight live with one screen and Jaws within the week.
        Assert.Equal(new BusinessGauges.Snapshot(Customers: 1, Employees: 1, DemoTheaters: 1, LiveTheaters: 1, LiveScreens: 1,
            ShowingsNext7Days: 1, GoLiveRequests: 1, FreeAdmissionRequests: 0, InvoicesOutstanding: 58m), snapshot);
        users.RecordObservableInstruments();
        Assert.Equal([("customer", 1d), ("employee", 1d)],
            users.GetMeasurementSnapshot().Select(m => ((string)m.Tags["kind"]!, m.Value)).OrderBy(x => x.Item1));
        owed.RecordObservableInstruments();
        Assert.Equal(58d, owed.LastMeasurement!.Value);
    }
}
