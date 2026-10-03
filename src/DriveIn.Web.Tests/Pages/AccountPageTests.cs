using DriveIn.Web.Authorization;
using DriveIn.Web.Components.Account.Pages;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace DriveIn.Web.Tests.Pages;

// Identity's statically rendered account pages: signing in, registering, and recovering a password.
public class AccountPageTests
{
    private static void Type<T>(IRenderedComponent<T> page, string id, string value) where T : Microsoft.AspNetCore.Components.IComponent =>
        page.Find($"[id='{id}']").Change(value);

    private static async Task<ApplicationUser?> FindAsync(TestApp app, string email)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
    }

    [Fact]
    public async Task Signing_in_with_the_right_password_sets_the_cookie_and_returns()
    {
        await using var app = new TestApp();
        await app.CreateUserAsync("pat@example.com");
        await using var host = new PageHost(app).UseRequest("POST");
        host.Nav.NavigateTo("Account/Login?ReturnUrl=%2Ftickets");
        var page = host.Render<Login>();

        Type(page, "Input.Email", "pat@example.com");
        Type(page, "Input.Password", "Password123!");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.EndsWith("/tickets", host.Nav.Uri));
        Assert.Contains(".AspNetCore.Identity.Application", host.ResponseCookies);
    }

    [Fact]
    public async Task A_wrong_password_is_refused()
    {
        await using var app = new TestApp();
        await app.CreateUserAsync("pat@example.com");
        await using var host = new PageHost(app).UseRequest();
        var page = host.Render<Login>();
        Assert.Contains("Use a local account to log in.", page.Text());

        Type(page, "Input.Email", "pat@example.com");
        Type(page, "Input.Password", "wrong");
        page.Find("form").Submit();

        page.WaitForText("Error: Invalid login attempt.");
        Assert.DoesNotContain(".AspNetCore.Identity.Application", host.ResponseCookies);
    }

    [Fact]
    public async Task A_login_without_an_email_is_not_tried()
    {
        await using var host = new PageHost().UseRequest("POST");
        var page = host.Render<Login>();

        page.Find("form").Submit();

        page.WaitForText("The Email field is required.");
    }

    // Records wrong passwords as Identity does for each failed sign-in.
    private static async Task FailSignInsAsync(TestApp app, string userId, int times)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        for (var i = 0; i < times; i++)
            await users.AccessFailedAsync((await users.FindByIdAsync(userId))!);
    }

    private static async Task<(bool Locked, int Failures)> LockStateAsync(TestApp app, string userId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(userId))!;
        return (await users.IsLockedOutAsync(user), await users.GetAccessFailedCountAsync(user));
    }

    [Fact]
    public async Task Too_many_wrong_passwords_in_a_row_lock_the_account()
    {
        await using var app = new TestApp();
        var pat = await app.CreateUserAsync("pat@example.com");
        await FailSignInsAsync(app, pat.Id, AppIdentityOptions.MaxFailedSignIns - 1);
        Assert.Equal((false, AppIdentityOptions.MaxFailedSignIns - 1), await LockStateAsync(app, pat.Id));
        await using var host = new PageHost(app).UseRequest("POST");
        var page = host.Render<Login>();

        Type(page, "Input.Email", "pat@example.com");
        Type(page, "Input.Password", "wrong");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.EndsWith("Account/Lockout", host.Nav.Uri));
        Assert.True((await LockStateAsync(app, pat.Id)).Locked);
        Assert.DoesNotContain(".AspNetCore.Identity.Application", host.ResponseCookies);
    }

    [Fact]
    public async Task A_right_password_before_the_limit_signs_in_and_clears_the_count()
    {
        await using var app = new TestApp();
        var pat = await app.CreateUserAsync("pat@example.com");
        await FailSignInsAsync(app, pat.Id, AppIdentityOptions.MaxFailedSignIns - 1);
        await using var host = new PageHost(app).UseRequest("POST");
        var page = host.Render<Login>();

        Type(page, "Input.Email", "pat@example.com");
        Type(page, "Input.Password", "Password123!");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains(".AspNetCore.Identity.Application", host.ResponseCookies));
        Assert.Equal((false, 0), await LockStateAsync(app, pat.Id));
    }

    [Fact]
    public async Task The_lockout_page_says_how_long_and_how_to_unlock()
    {
        await using var host = new PageHost().UseRequest();
        var page = host.Render<Lockout>();

        Assert.Contains($"After {AppIdentityOptions.MaxFailedSignIns} sign-in attempts", page.Text());
        Assert.Contains($"locked for {(int)AppIdentityOptions.LockoutDuration.TotalMinutes} minutes", page.Text());
        Assert.NotNull(page.Find("a[href='Account/ForgotPassword']"));
    }

    [Fact]
    public async Task A_locked_out_account_is_sent_to_the_lockout_page()
    {
        await using var app = new TestApp();
        var pat = await app.CreateUserAsync("pat@example.com");
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(pat.Id))!;
            await users.SetLockoutEnabledAsync(user, true);
            await users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
        }
        await using var host = new PageHost(app).UseRequest("POST");
        var page = host.Render<Login>();

        Type(page, "Input.Email", "pat@example.com");
        Type(page, "Input.Password", "Password123!");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.EndsWith("Account/Lockout", host.Nav.Uri));
    }

    [Fact]
    public async Task Registering_creates_the_account_and_emails_a_confirmation_link()
    {
        await using var app = new TestApp();
        await using var host = new PageHost(app).UseRequest("POST");
        var page = host.Render<Register>();

        Type(page, "Input.Email", "new@example.com");
        Type(page, "Input.Password", "Password123!");
        Type(page, "Input.ConfirmPassword", "Password123!");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("Account/RegisterConfirmation?email=new%40example.com", host.Nav.Uri));
        Assert.NotNull(await FindAsync(app, "new@example.com"));
        Assert.Contains(app.Email.Sent, m => m.To == "new@example.com" && m.Body.Contains("Account/ConfirmEmail"));
    }

    [Fact]
    public async Task Registering_a_taken_email_shows_why()
    {
        await using var app = new TestApp();
        await app.CreateUserAsync("pat@example.com");
        await using var host = new PageHost(app).UseRequest("POST");
        var page = host.Render<Register>();

        Type(page, "Input.Email", "pat@example.com");
        Type(page, "Input.Password", "Password123!");
        Type(page, "Input.ConfirmPassword", "Password123!");
        page.Find("form").Submit();

        page.WaitForText("is already taken");
    }
}
