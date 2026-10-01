using DriveIn.Web.Components.Pages.Admin;
using DriveIn.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Tests.Pages;

// The admin invoice, subscriptions and billing report pages.
public class AdminInvoicePageTests
{
    private static async Task<Invoice> ReloadAsync(PageData.Billed b)
    {
        await using var db = b.App.Db();
        return await db.Invoices.Include(i => i.Lines).Include(i => i.Payments).SingleAsync();
    }

    [Fact]
    public async Task A_draft_gets_a_credit_line_and_is_issued()
    {
        await using var b = await PageData.BilledTheaterAsync(issue: false);
        await using var host = new PageHost(b.App).SignIn(b.AdminUser, admin: true);
        var page = host.Render<AdminInvoice>(p => p.Add(x => x.Id, b.Invoice.Id));
        page.WaitForText("Add a line");

        page.SetField("Description", "Opening credit");
        page.SetField("Price each", "-10");
        page.ClickButton("Add line");
        page.WaitForText("Opening credit");
        Assert.Contains("Total $88.00", page.Text());
        page.ClickButton("Remove Opening credit");
        page.WaitForAssertion(() => Assert.DoesNotContain("Opening credit", page.Text()));
        page.ClickButton("Issue and email");

        page.WaitForText("Issued and emailed.");
        Assert.Equal(InvoiceStatus.Issued, (await ReloadAsync(b)).Status);
    }

    [Fact]
    public async Task A_payment_is_recorded_and_the_receipt_resent()
    {
        await using var b = await PageData.BilledTheaterAsync();
        await using var host = new PageHost(b.App).SignIn(b.AdminUser, admin: true);
        var page = host.Render<AdminInvoice>(p => p.Add(x => x.Id, b.Invoice.Id));
        page.WaitForText("Record a payment");

        page.SetField("Reference (check no.)", "1234");
        page.ClickButton("Record payment");

        page.WaitForText("Payment recorded and receipt emailed.");
        Assert.Contains("Payments received", page.Text());
        Assert.Equal(InvoiceStatus.Paid, (await ReloadAsync(b)).Status);
        page.ClickButton("Resend receipt");
        page.WaitForText("Sent.");
    }

    [Fact]
    public async Task An_issued_invoice_is_voided_with_a_reason()
    {
        await using var b = await PageData.BilledTheaterAsync();
        await using var host = new PageHost(b.App).SignIn(b.AdminUser, admin: true);
        var page = host.Render<AdminInvoice>(p => p.Add(x => x.Id, b.Invoice.Id));
        page.WaitForText("Resend invoice");
        page.ClickButton("Resend invoice");
        page.WaitForText("Sent.");

        page.ClickButton("Void…");
        page.SetField("Reason for voiding", "Billed in error");
        page.ClickButton("Void invoice");

        page.WaitForText("Voided.");
        Assert.Contains("This invoice is void and nothing is owed on it. Billed in error", page.Text());
        page.ClickButton("Print");
        host.Context.JSInterop.VerifyInvoke("print");
    }

    // --- Subscriptions ---

    [Fact]
    public async Task A_subscriptions_price_is_changed_and_it_is_cancelled_and_reactivated()
    {
        await using var b = await PageData.BilledTheaterAsync();
        await using var host = new PageHost(b.App).SignIn(b.AdminUser, admin: true);
        var page = host.Render<AdminBillingSubscriptions>();
        page.WaitForText("Starlight owner@example.com 2 Active $49.00 $98.00");

        page.ClickButton("Price…");
        page.SetField("Price per screen", "59");
        page.ClickButton("Save price");
        page.WaitForText("Starlight's price is now $59.00 per screen from the next invoice drafted.");

        page.ClickButton("Cancel…");
        page.ClickButton("Keep");
        page.ClickButton("Cancel…");
        page.ClickButton("Cancel subscription");
        page.WaitForText("Cancelled. Starlight is billed through this month");
        page.ClickButton("Reactivate");

        page.WaitForText("Starlight's subscription is active again.");
        await using var db = b.App.Db();
        var sub = await db.Subscriptions.SingleAsync();
        Assert.Equal((59m, true), (sub.PricePerScreenPerMonth, sub.IsActive));
    }

    [Fact]
    public async Task Billing_is_started_for_an_unbilled_theater()
    {
        await using var app = new TestApp();
        var admin = await app.CreateUserAsync("admin@example.com", admin: true);
        await app.CreateTheaterAsync("Moonlight");
        await using var host = new PageHost(app).SignIn(admin, admin: true);
        var page = host.Render<AdminBillingSubscriptions>();
        page.WaitForText("Not billed");

        page.ClickButton("Start…");
        page.ClickButton("Start billing");

        page.WaitForText("Billing started for Moonlight.");
        Assert.Contains("Active", page.Text());
    }

    // --- Reports ---

    [Fact]
    public async Task The_billing_report_shows_subscriptions_invoicing_and_whats_owed()
    {
        await using var b = await PageData.BilledTheaterAsync();
        await using var host = new PageHost(b.App).SignIn(b.AdminUser, admin: true);

        var page = host.Render<AdminBillingReports>();

        page.WaitForText("Subscriptions 1 active, 2 screens");
        var text = page.Text();
        Assert.Contains("Invoiced $98.00 for Jan 2026 – Sep 2026", text);
        Assert.Contains("Owed today $98.00 none overdue", text);
        Assert.Contains("Starlight 1 $98.00 $0.00 $98.00", text);
        Assert.Contains("admin/billing/aging.csv?from=2026-01&amp;to=2026-09", page.Markup);

        page.ClickButton("Show");
        page.WaitForText("Subscriptions 1");
    }
}
