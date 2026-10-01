using DriveIn.Web.Components.Pages.Admin;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DriveIn.Web.Tests.Pages;

public class AdminUsersAndBillingPageTests
{
    private static async Task<(PageHost Host, IRenderedComponent<AdminUsers> Page)> UsersAsync()
    {
        var app = new TestApp();
        var admin = await app.CreateUserAsync("admin@example.com", admin: true);
        await app.CreateUserAsync("pat@example.com");
        var host = new PageHost(app).SignIn(admin, admin: true);
        var page = host.Render<AdminUsers>();
        page.WaitForText("pat@example.com");
        return (host, page);
    }

    private static async Task<ApplicationUser> FindAsync(TestApp app, string email)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email))!;
    }

    private static void ClickInRow(IRenderedComponent<AdminUsers> page, string email, string button) =>
        page.FindAll("tr").Single(r => r.TextContent.Contains(email))
            .QuerySelectorAll("button").Single(b => b.TextContent.Trim() == button).Click();

    [Fact]
    public async Task Users_are_listed_and_searched()
    {
        var (host, page) = await UsersAsync();
        await using var _ = host;
        Assert.Contains("admin@example.com", page.Text());

        page.SetField("Search users", "pat");
        page.FindAll("form")[1].Submit();

        page.WaitForAssertion(() => Assert.DoesNotContain("admin@example.com", page.Text()));
        Assert.Contains("pat@example.com", page.Text());

        page.SetField("Search users", "nobody");
        page.FindAll("form")[1].Submit();
        page.WaitForText("No users match.");
    }

    [Fact]
    public async Task An_admin_creates_a_user()
    {
        var (host, page) = await UsersAsync();
        await using var _ = host;

        page.FindAll("form")[0].Submit();
        page.WaitForText("Enter an email address.");
        page.SetField("Email", "new@example.com");
        page.SetField("Display name (optional)", "Newbie");
        page.FindAll("form")[0].Submit();

        page.WaitForText("User created and emailed a link to set their password.");
        Assert.Contains("new@example.com Newbie", page.Text());
        Assert.Contains(host.App.Email.Sent, m => m.To == "new@example.com");
    }

    [Fact]
    public async Task A_users_name_admin_rights_and_lock_are_changed()
    {
        var (host, page) = await UsersAsync();
        await using var _ = host;

        ClickInRow(page, "pat@example.com", "Edit");
        page.SetField("Display name", "Pat");
        page.ClickButton("Save");
        page.WaitForText("pat@example.com Pat");

        ClickInRow(page, "pat@example.com", "Make admin");
        page.WaitForText("pat@example.com is now an admin.");
        ClickInRow(page, "pat@example.com", "Remove admin");
        page.WaitForText("pat@example.com is no longer an admin.");

        ClickInRow(page, "pat@example.com", "Lock");
        page.WaitForText("pat@example.com is locked out.");
        Assert.Contains("Locked", page.Text());
        ClickInRow(page, "pat@example.com", "Unlock");
        page.WaitForText("pat@example.com is unlocked.");

        Assert.Equal("Pat", (await FindAsync(host.App, "pat@example.com")).DisplayName);
    }

    [Fact]
    public async Task A_user_is_sent_a_reset_or_deleted()
    {
        var (host, page) = await UsersAsync();
        await using var _ = host;

        ClickInRow(page, "pat@example.com", "Reset password");
        page.WaitForText("Password reset email sent to pat@example.com.");

        ClickInRow(page, "pat@example.com", "Delete");
        ClickInRow(page, "pat@example.com", "Confirm delete");

        page.WaitForText("Deleted pat@example.com.");
        Assert.Null(await FindAsync(host.App, "pat@example.com"));
    }

    // --- Billing ---

    [Fact]
    public async Task Drafts_are_issued_from_the_invoice_list()
    {
        await using var b = await PageData.BilledTheaterAsync(issue: false);
        await using var host = new PageHost(b.App).SignIn(b.AdminUser, admin: true);
        var page = host.Render<AdminBilling>();
        page.WaitForText("Issue all drafts (1)");

        page.ClickButton("Check for new drafts now");
        page.WaitForText("No new invoices are due.");
        page.ClickButton("Issue all drafts (1)");

        page.WaitForText("Issued 1 invoice.");
        Assert.Contains("Starlight Sep 2026", page.Text());
        Assert.Contains("$98.00 owed on those issued", page.Text());
        await using var db = b.App.Db();
        Assert.Equal(InvoiceStatus.Issued, (await db.Invoices.SingleAsync()).Status);
    }

    [Fact]
    public async Task Invoices_are_filtered_by_status_and_overdue()
    {
        await using var b = await PageData.BilledTheaterAsync();
        await using var host = new PageHost(b.App).SignIn(b.AdminUser, admin: true);
        var page = host.Render<AdminBilling>();
        page.WaitForText("1 invoice");

        host.Select(page, "Status", "Paid");
        page.WaitForText("No invoices match.");
        host.Select(page, "Status", "Issued");
        page.WaitForText("1 invoice");

        page.Check("Overdue only");
        page.WaitForText("No invoices match."); // due in the future
    }
}
