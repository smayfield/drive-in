using DriveIn.Web.Components.Account;
using DriveIn.Web.Components.Account.Pages.Manage;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using ManageIndex = DriveIn.Web.Components.Account.Pages.Manage.Index;

namespace DriveIn.Web.Tests.Pages;

// Identity's account management pages, for a signed-in user.
public class AccountManagePageTests
{
    private sealed record Account(TestApp App, ApplicationUser User, PageHost Host) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
        }
    }

    private static async Task<Account> SignedInAsync(string method = "GET", bool twoFactor = false)
    {
        var app = new TestApp();
        var user = await app.CreateUserAsync("pat@example.com");
        if (twoFactor)
            await WithUsersAsync(app, async users => (await users.SetTwoFactorEnabledAsync((await users.FindByIdAsync(user.Id))!, true)).Succeeded);
        return new Account(app, user, new PageHost(app).SignIn(user).UseRequest(method));
    }

    private static void Type<T>(IRenderedComponent<T> page, string id, string value) where T : IComponent =>
        page.Find($"[id='{id}']").Change(value);

    private static async Task<T> WithUsersAsync<T>(TestApp app, Func<UserManager<ApplicationUser>, Task<T>> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());
    }

    private static Task<ApplicationUser> ReloadAsync(Account a) =>
        WithUsersAsync(a.App, async users => (await users.FindByIdAsync(a.User.Id))!);

    [Fact]
    public async Task The_profile_saves_a_phone_number()
    {
        await using var a = await SignedInAsync("POST");
        a.Host.Nav.NavigateTo("Account/Manage");
        var page = a.Host.Render<ManageIndex>();
        Assert.Contains("pat@example.com", page.Markup);

        Type(page, "Input.PhoneNumber", "512-555-0100");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.EndsWith("Account/Manage", a.Host.Nav.Uri));
        Assert.Contains(IdentityRedirectManager.StatusCookieName, a.Host.ResponseCookies);
        Assert.Equal("512-555-0100", (await ReloadAsync(a)).PhoneNumber);
    }

    [Fact]
    public async Task A_page_for_a_user_who_no_longer_exists_goes_to_invalid_user()
    {
        await using var host = new PageHost().SignIn(Principals.Create("gone")).UseRequest();

        host.Render<ManageIndex>();

        Assert.EndsWith("Account/InvalidUser", host.Nav.Uri);
    }

    [Fact]
    public async Task Changing_email_sends_a_link_to_the_new_address()
    {
        await using var a = await SignedInAsync("POST");
        var page = a.Host.Render<Email>();

        page.Find("form:not(#send-verification-form)").Submit();
        page.WaitForText("Your email is unchanged.");

        Type(page, "Input.NewEmail", "pat@new.example.com");
        page.Find("form:not(#send-verification-form)").Submit();

        page.WaitForText("Confirmation link to change email sent.");
        Assert.Contains(a.App.Email.Sent, m => m.To == "pat@new.example.com" && m.Body.Contains("Account/ConfirmEmailChange"));
    }

    [Fact]
    public async Task An_unconfirmed_email_can_be_sent_a_verification_link()
    {
        await using var a = await SignedInAsync("POST");
        await WithUsersAsync(a.App, async users =>
        {
            var user = (await users.FindByIdAsync(a.User.Id))!;
            user.EmailConfirmed = false;
            return (await users.UpdateAsync(user)).Succeeded;
        });
        var page = a.Host.Render<Email>();

        page.Find("#send-verification-form").Submit();

        page.WaitForText("Verification email sent. Please check your email.");
        Assert.Contains(a.App.Email.Sent, m => m.Body.Contains("Account/ConfirmEmail"));
    }

    [Fact]
    public async Task The_password_is_changed_with_the_current_one()
    {
        await using var a = await SignedInAsync("POST");
        a.Host.Nav.NavigateTo("Account/Manage/ChangePassword");
        var page = a.Host.Render<ChangePassword>();

        Type(page, "Input.OldPassword", "wrong");
        Type(page, "Input.NewPassword", "NewPassword456!");
        Type(page, "Input.ConfirmPassword", "NewPassword456!");
        page.Find("form").Submit();
        page.WaitForText("Error: Incorrect password.");

        Type(page, "Input.OldPassword", "Password123!");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.EndsWith("Account/Manage/ChangePassword", a.Host.Nav.Uri));
        Assert.True(await WithUsersAsync(a.App, async users => await users.CheckPasswordAsync((await users.FindByIdAsync(a.User.Id))!, "NewPassword456!")));
    }

    [Fact]
    public async Task An_account_without_a_password_sets_one()
    {
        await using var a = await SignedInAsync("POST");
        await WithUsersAsync(a.App, async users => (await users.RemovePasswordAsync((await users.FindByIdAsync(a.User.Id))!)).Succeeded);
        a.Host.Nav.NavigateTo("Account/Manage/SetPassword");
        var page = a.Host.Render<SetPassword>();

        Type(page, "Input.NewPassword", "NewPassword456!");
        Type(page, "Input.ConfirmPassword", "NewPassword456!");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.EndsWith("Account/Manage/SetPassword", a.Host.Nav.Uri));
        Assert.True(await WithUsersAsync(a.App, async users => await users.HasPasswordAsync((await users.FindByIdAsync(a.User.Id))!)));
    }

    [Fact]
    public async Task Password_pages_send_you_to_the_one_that_fits()
    {
        await using var a = await SignedInAsync();

        a.Host.Render<SetPassword>();

        Assert.EndsWith("Account/Manage/ChangePassword", a.Host.Nav.Uri);
    }

    [Fact]
    public async Task Deleting_the_account_needs_the_password()
    {
        await using var a = await SignedInAsync("POST");
        a.Host.Nav.NavigateTo("Account/Manage/DeletePersonalData");
        var page = a.Host.Render<DeletePersonalData>();

        Type(page, "Input.Password", "wrong");
        page.Find("form").Submit();
        page.WaitForText("Error: Incorrect password.");

        Type(page, "Input.Password", "Password123!");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.EndsWith("Account/Manage/DeletePersonalData", a.Host.Nav.Uri));
        Assert.Null(await WithUsersAsync(a.App, users => users.FindByIdAsync(a.User.Id)));
    }

    [Fact]
    public async Task Two_factor_settings_show_whats_set_up()
    {
        await using var a = await SignedInAsync(twoFactor: true);

        var page = a.Host.Render<TwoFactorAuthentication>();

        Assert.Contains("You have no recovery codes left.", page.Text());
        Assert.Contains("Disable 2FA", page.Text());
    }

    [Fact]
    public async Task Without_two_factor_an_authenticator_app_can_be_added()
    {
        await using var a = await SignedInAsync();

        var page = a.Host.Render<TwoFactorAuthentication>();

        Assert.Contains("Add authenticator app", page.Text());
    }

    [Fact]
    public async Task The_authenticator_setup_shows_a_key_and_refuses_a_wrong_code()
    {
        await using var a = await SignedInAsync("POST");
        var page = a.Host.Render<EnableAuthenticator>();
        Assert.Contains("otpauth://totp/", page.Markup);

        Type(page, "Input.Code", "000000");
        page.Find("form").Submit();

        page.WaitForText("Error: Verification code is invalid.");
    }

    [Fact]
    public async Task Two_factor_is_disabled()
    {
        await using var a = await SignedInAsync("POST", twoFactor: true);
        var page = a.Host.Render<Disable2fa>();

        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.EndsWith("Account/Manage/TwoFactorAuthentication", a.Host.Nav.Uri));
        Assert.False((await ReloadAsync(a)).TwoFactorEnabled);
    }

    [Fact]
    public async Task New_recovery_codes_are_generated()
    {
        await using var a = await SignedInAsync("POST", twoFactor: true);
        var page = a.Host.Render<GenerateRecoveryCodes>();

        page.Find("form").Submit();

        page.WaitForText("You have generated new recovery codes.");
        Assert.Equal(10, await WithUsersAsync(a.App, async users => await users.CountRecoveryCodesAsync((await users.FindByIdAsync(a.User.Id))!)));
    }

    [Fact]
    public async Task The_authenticator_key_is_reset()
    {
        await using var a = await SignedInAsync("POST", twoFactor: true);
        var page = a.Host.Render<ResetAuthenticator>();

        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.EndsWith("Account/Manage/EnableAuthenticator", a.Host.Nav.Uri));
        Assert.False((await ReloadAsync(a)).TwoFactorEnabled);
    }

    [Fact]
    public async Task Personal_data_can_be_downloaded()
    {
        await using var a = await SignedInAsync();

        var page = a.Host.Render<PersonalData>();

        Assert.Contains("Account/Manage/DownloadPersonalData", page.Markup);
    }
}
