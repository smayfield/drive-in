using System.Security.Claims;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Tests.Pages;

// Data shared by page tests that TicketSalesTests.SetUpAsync doesn't cover.
internal static class PageData
{
    internal sealed record Billed(TestApp App, ApplicationUser Owner, ApplicationUser AdminUser, Theater Theater, Invoice Invoice) : IAsyncDisposable
    {
        public ClaimsPrincipal Admin => Principals.For(AdminUser, admin: true);
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    // A signed-up theater (two screens, Chicago) activated by an admin, so it has a subscription; with issue, its
    // September invoice is issued and emailed, otherwise it's left a draft.
    internal static async Task<Billed> BilledTheaterAsync(bool issue = true)
    {
        var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var adminUser = await app.CreateUserAsync("admin@example.com", admin: true);
        var admin = Principals.For(adminUser, admin: true);
        var onboarding = app.Get<OnboardingService>();
        var theater = await onboarding.CreateDemoTheaterAsync(Principals.For(owner),
            new NewTheaterInput("Starlight", "Austin", "TX", "America/Chicago", 2, true));
        await onboarding.ActivateAsync(admin, theater.Id, TestApp.BaseUri);
        var billing = app.Get<BillingService>();
        await billing.GenerateDraftsAsync(admin);
        if (issue)
            await billing.IssueAllDraftsAsync(admin, TestApp.BaseUri);
        await using var db = app.Db();
        var invoice = await db.Invoices.SingleAsync(i => i.TheaterId == theater.Id);
        return new Billed(app, owner, adminUser, theater, invoice);
    }
}
