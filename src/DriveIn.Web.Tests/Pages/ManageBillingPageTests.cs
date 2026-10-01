using DriveIn.Web.Components.Pages.Manage;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Authorization.TheaterPermissions;

namespace DriveIn.Web.Tests.Pages;

public class ManageBillingPageTests
{
    [Fact]
    public async Task The_owner_sees_the_plan_and_whats_due()
    {
        await using var b = await PageData.BilledTheaterAsync();
        await using var host = new PageHost(b.App).SignIn(b.Owner);

        var page = host.Render<ManageBilling>(p => p.Add(x => x.Id, b.Theater.Id));

        page.WaitForText("Standard plan");
        var text = page.Text();
        Assert.Contains("2 screens × $49.00 = $98.00 a month", text);
        Assert.Contains("Invoices go to owner@example.com.", text);
        Assert.Contains("$98.00 is due.", text);
        Assert.Contains("Sep 2026", text);
        Assert.Contains($"manage/{b.Theater.Id}/billing/invoices/{b.Invoice.Id}", page.Markup);
    }

    [Fact]
    public async Task The_billing_email_is_changed()
    {
        await using var b = await PageData.BilledTheaterAsync();
        await using var host = new PageHost(b.App).SignIn(b.Owner);
        var page = host.Render<ManageBilling>(p => p.Add(x => x.Id, b.Theater.Id));
        page.WaitForText("Billing email");

        page.SetField("Billing email", "books@example.com");
        page.ClickButton("Save");

        page.WaitForText("Saved.");
        Assert.Contains("Invoices go to books@example.com.", page.Text());
    }

    [Fact]
    public async Task The_owner_cancels_after_confirming()
    {
        await using var b = await PageData.BilledTheaterAsync();
        await using var host = new PageHost(b.App).SignIn(b.Owner);
        var page = host.Render<ManageBilling>(p => p.Add(x => x.Id, b.Theater.Id));
        page.WaitForText("Cancel subscription…");

        page.ClickButton("Cancel subscription…");
        page.ClickButton("Keep it");
        page.ClickButton("Cancel subscription…");
        page.ClickButton("Yes, cancel the subscription");

        page.WaitForText("Your subscription is cancelled.");
        Assert.Contains("Cancelled", page.Text());
        await using var db = b.App.Db();
        Assert.False((await db.Subscriptions.SingleAsync()).IsActive);
    }

    [Fact]
    public async Task Staff_who_can_view_billing_cant_change_it()
    {
        await using var b = await PageData.BilledTheaterAsync();
        var books = await b.App.CreateUserAsync("books@example.com", b.Theater.Id);
        await b.App.GrantAsync(books, ViewBilling);
        await using var host = new PageHost(b.App).SignIn(books);

        var page = host.Render<ManageBilling>(p => p.Add(x => x.Id, b.Theater.Id));

        page.WaitForText("$98.00 is due.");
        Assert.DoesNotContain("Cancel subscription", page.Text());
        Assert.DoesNotContain("Billing email", page.Text());
    }

    [Fact]
    public async Task A_demo_theater_isnt_billed_yet()
    {
        var s = await TicketSalesTests.SetUpAsync();
        await using var db = s.App.Db();
        (await db.Theaters.SingleAsync()).Mode = Data.TheaterMode.Demo;
        await db.SaveChangesAsync();
        await using var host = new PageHost(s.App).SignIn(s.Owner);

        var page = host.Render<ManageBilling>(p => p.Add(x => x.Id, s.Theater.Id));

        page.WaitForText("Your theater is in demo mode, which is free.");
        Assert.Contains("No invoices yet.", page.Text());
    }

    [Fact]
    public async Task An_invoice_is_shown_ready_to_print()
    {
        await using var b = await PageData.BilledTheaterAsync();
        await using var host = new PageHost(b.App).SignIn(b.Owner);

        var page = host.Render<ManageInvoice>(p => p.Add(x => x.Id, b.Theater.Id).Add(x => x.InvoiceId, b.Invoice.Id));

        page.WaitForText($"Invoice {b.Invoice.DisplayNumber}");
        var text = page.Text();
        Assert.Contains("Starlight", text);
        Assert.Contains("Total $98.00", text);
        Assert.Contains($"Please pay $98.00", text);
        page.ClickButton("Print or save as PDF");
        host.Context.JSInterop.VerifyInvoke("print");
    }

    [Fact]
    public async Task Another_theaters_invoice_is_not_found()
    {
        await using var b = await PageData.BilledTheaterAsync();
        var other = await b.App.CreateTheaterAsync("Other", b.Owner.Id);
        await using var host = new PageHost(b.App).SignIn(b.Owner);

        var page = host.Render<ManageInvoice>(p => p.Add(x => x.Id, other.Id).Add(x => x.InvoiceId, b.Invoice.Id));

        page.WaitForAssertion(() => Assert.Contains("mud-alert-text-error", page.Markup));
    }
}
