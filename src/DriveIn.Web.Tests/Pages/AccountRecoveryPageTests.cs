using System.Text;
using DriveIn.Web.Components.Account;
using DriveIn.Web.Components.Account.Pages;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace DriveIn.Web.Tests.Pages;

// Identity's pages for recovering a password and confirming an email, and its plain message pages.
public class AccountRecoveryPageTests
{
    private static void Type<T>(IRenderedComponent<T> page, string id, string value) where T : IComponent =>
        page.Find($"[id='{id}']").Change(value);

    private static string Encode(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

    private static async Task<T> WithUsersAsync<T>(TestApp app, Func<UserManager<ApplicationUser>, Task<T>> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());
    }

    [Fact]
    public async Task Forgot_password_emails_a_reset_link_to_a_confirmed_account()
    {
        await using var app = new TestApp();
        await app.CreateUserAsync("pat@example.com");
        await using var host = new PageHost(app).UseRequest("POST");
        var page = host.Render<ForgotPassword>();

        Type(page, "Input.Email", "pat@example.com");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.EndsWith("Account/ForgotPasswordConfirmation", host.Nav.Uri));
        Assert.Contains(app.Email.Sent, m => m.To == "pat@example.com" && m.Body.Contains("Account/ResetPassword"));
    }

    [Fact]
    public async Task Forgot_password_doesnt_reveal_an_unknown_email()
    {
        await using var app = new TestApp();
        await using var host = new PageHost(app).UseRequest("POST");
        var page = host.Render<ForgotPassword>();

        Type(page, "Input.Email", "nobody@example.com");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.EndsWith("Account/ForgotPasswordConfirmation", host.Nav.Uri));
        Assert.Empty(app.Email.Sent);
    }

    [Fact]
    public async Task A_reset_link_sets_a_new_password()
    {
        await using var app = new TestApp();
        var pat = await app.CreateUserAsync("pat@example.com");
        var token = await WithUsersAsync(app, async users => await users.GeneratePasswordResetTokenAsync((await users.FindByIdAsync(pat.Id))!));
        await using var host = new PageHost(app).UseRequest("POST");
        host.Nav.NavigateTo($"Account/ResetPassword?code={Encode(token)}");
        var page = host.Render<ResetPassword>();

        Type(page, "Input.Email", "pat@example.com");
        Type(page, "Input.Password", "NewPassword456!");
        Type(page, "Input.ConfirmPassword", "NewPassword456!");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.EndsWith("Account/ResetPasswordConfirmation", host.Nav.Uri));
        Assert.True(await WithUsersAsync(app, async users => await users.CheckPasswordAsync((await users.FindByIdAsync(pat.Id))!, "NewPassword456!")));
    }

    [Theory]
    [InlineData(false, false)] // locked by wrong passwords: resetting unlocks it
    [InlineData(true, true)]   // locked by an admin: it stays locked
    public async Task Resetting_a_password_lifts_a_lockout_from_wrong_passwords_but_not_an_admins_lock(bool lockedByAdmin, bool lockedAfter)
    {
        await using var app = new TestApp();
        var pat = await app.CreateUserAsync("pat@example.com");
        var token = await WithUsersAsync(app, async users =>
        {
            var user = (await users.FindByIdAsync(pat.Id))!;
            if (lockedByAdmin)
                await users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
            else
                for (var i = 0; i < DriveIn.Web.Authorization.AppIdentityOptions.MaxFailedSignIns; i++)
                    await users.AccessFailedAsync(user);
            Assert.True(await users.IsLockedOutAsync(user));
            return await users.GeneratePasswordResetTokenAsync(user);
        });
        await using var host = new PageHost(app).UseRequest("POST");
        host.Nav.NavigateTo($"Account/ResetPassword?code={Encode(token)}");
        var page = host.Render<ResetPassword>();

        Type(page, "Input.Email", "pat@example.com");
        Type(page, "Input.Password", "NewPassword456!");
        Type(page, "Input.ConfirmPassword", "NewPassword456!");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.EndsWith("Account/ResetPasswordConfirmation", host.Nav.Uri));
        Assert.Equal(lockedAfter, await WithUsersAsync(app, async users => await users.IsLockedOutAsync((await users.FindByIdAsync(pat.Id))!)));
    }

    [Fact]
    public async Task A_bad_reset_link_is_refused()
    {
        await using var app = new TestApp();
        await app.CreateUserAsync("pat@example.com");
        await using var host = new PageHost(app).UseRequest("POST");
        host.Nav.NavigateTo($"Account/ResetPassword?code={Encode("forged")}");
        var page = host.Render<ResetPassword>();

        Type(page, "Input.Email", "pat@example.com");
        Type(page, "Input.Password", "NewPassword456!");
        Type(page, "Input.ConfirmPassword", "NewPassword456!");
        page.Find("form").Submit();

        page.WaitForText("Error: Invalid token.");
    }

    [Fact]
    public async Task A_reset_page_without_a_code_redirects()
    {
        await using var host = new PageHost().UseRequest();

        host.Render<ResetPassword>();

        Assert.EndsWith("Account/InvalidPasswordReset", host.Nav.Uri);
    }

    [Fact]
    public async Task A_confirmation_link_confirms_the_email()
    {
        await using var app = new TestApp();
        var pat = await app.CreateUserAsync("pat@example.com");
        var token = await WithUsersAsync(app, async users =>
        {
            var user = (await users.FindByIdAsync(pat.Id))!;
            user.EmailConfirmed = false;
            await users.UpdateAsync(user);
            return await users.GenerateEmailConfirmationTokenAsync(user);
        });
        await using var host = new PageHost(app).UseRequest();
        host.Nav.NavigateTo($"Account/ConfirmEmail?userId={pat.Id}&code={Encode(token)}");

        var page = host.Render<ConfirmEmail>();

        page.WaitForText("Thank you for confirming your email.");
        Assert.True(await WithUsersAsync(app, async users => (await users.FindByIdAsync(pat.Id))!.EmailConfirmed));
    }

    [Fact]
    public async Task A_confirmation_link_for_nobody_is_not_found()
    {
        await using var host = new PageHost().UseRequest();
        host.Nav.NavigateTo($"Account/ConfirmEmail?userId=nobody&code={Encode("x")}");

        var page = host.Render<ConfirmEmail>();

        page.WaitForText("Error loading user with ID nobody");
        Assert.Equal(404, host.Request.Response.StatusCode);
    }

    [Fact]
    public async Task A_confirmation_email_is_sent_again_on_request()
    {
        await using var app = new TestApp();
        await app.CreateUserAsync("pat@example.com");
        await using var host = new PageHost(app).UseRequest("POST");
        var page = host.Render<ResendEmailConfirmation>();

        Type(page, "Input.Email", "pat@example.com");
        page.Find("form").Submit();

        page.WaitForText("Verification email sent. Please check your email.");
        Assert.Single(app.Email.Sent);
    }

    [Fact]
    public async Task An_email_change_link_changes_the_email_and_user_name()
    {
        await using var app = new TestApp();
        var pat = await app.CreateUserAsync("pat@example.com");
        var token = await WithUsersAsync(app, async users =>
            await users.GenerateChangeEmailTokenAsync((await users.FindByIdAsync(pat.Id))!, "pat@new.example.com"));
        await using var host = new PageHost(app).SignIn(pat).UseRequest();
        host.Nav.NavigateTo($"Account/ConfirmEmailChange?userId={pat.Id}&email=pat%40new.example.com&code={Encode(token)}");

        var page = host.Render<ConfirmEmailChange>();

        page.WaitForText("Thank you for confirming your email change.");
        var changed = await WithUsersAsync(app, users => users.FindByIdAsync(pat.Id));
        Assert.Equal(("pat@new.example.com", "pat@new.example.com"), (changed!.Email, changed.UserName));
    }

    [Fact]
    public async Task An_incomplete_email_change_link_goes_to_login_with_an_error()
    {
        await using var host = new PageHost().UseRequest();

        host.Render<ConfirmEmailChange>();

        Assert.EndsWith("Account/Login", host.Nav.Uri);
        Assert.Contains(IdentityRedirectManager.StatusCookieName, host.ResponseCookies);
    }

    [Theory]
    [InlineData(typeof(AccessDenied), "Access denied")]
    [InlineData(typeof(ForgotPasswordConfirmation), "Forgot password confirmation")]
    [InlineData(typeof(InvalidPasswordReset), "Invalid password reset")]
    [InlineData(typeof(InvalidUser), "Invalid user")]
    [InlineData(typeof(Lockout), "Locked out")]
    [InlineData(typeof(ResetPasswordConfirmation), "Reset password confirmation")]
    [InlineData(typeof(RegisterConfirmation), "Check your email")]
    public async Task Message_pages_render(Type page, string expected)
    {
        await using var host = new PageHost().UseRequest();

        var rendered = host.Context.Render<DynamicComponent>(p => p.Add(x => x.Type, page));

        Assert.Contains(expected, rendered.Text());
    }

    [Fact]
    public async Task A_status_message_left_in_a_cookie_is_shown_once()
    {
        await using var host = new PageHost().UseRequest();
        host.Request.Request.Headers.Cookie = $"{IdentityRedirectManager.StatusCookieName}=Saved.";

        var page = host.Render<Login>();

        Assert.Contains("Saved.", page.Text());
        Assert.Contains(IdentityRedirectManager.StatusCookieName, host.ResponseCookies); // deleted
    }
}
