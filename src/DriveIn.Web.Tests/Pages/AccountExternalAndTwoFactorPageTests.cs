using System.Security.Claims;
using DriveIn.Web.Components.Account;
using DriveIn.Web.Components.Account.Pages;
using DriveIn.Web.Components.Account.Pages.Manage;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace DriveIn.Web.Tests.Pages;

// Signing in with Google (the external login callback) and with a second factor.
public class AccountExternalAndTwoFactorPageTests
{
    private const string GoogleKey = "google-123";

    private static async Task<T> WithUsersAsync<T>(TestApp app, Func<UserManager<ApplicationUser>, Task<T>> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());
    }

    // What the Google handler leaves in the external cookie on its way back to the callback.
    private static Task<PageHost> FromGoogleAsync(PageHost host, string? email = "pat@example.com", bool verified = true)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, GoogleKey), new(ClaimTypes.Name, "Pat Google")];
        if (email is not null)
            claims.Add(new Claim(ClaimTypes.Email, email));
        claims.Add(new Claim("email_verified", verified ? "true" : "false"));
        var properties = new AuthenticationProperties();
        properties.Items["LoginProvider"] = "Google";
        return host.CarryCookieAsync(IdentityConstants.ExternalScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, "Google")), properties);
    }

    private static IRenderedComponent<ExternalLogin> Callback(PageHost host, string? returnUrl = null)
    {
        host.Nav.NavigateTo($"Account/ExternalLogin?Action=LoginCallback{(returnUrl is null ? "" : $"&ReturnUrl={Uri.EscapeDataString(returnUrl)}")}");
        return host.Render<ExternalLogin>();
    }

    [Fact]
    public async Task A_returning_google_user_is_signed_in()
    {
        await using var app = new TestApp();
        var pat = await app.CreateUserAsync("pat@example.com");
        await WithUsersAsync(app, async users =>
            (await users.AddLoginAsync((await users.FindByIdAsync(pat.Id))!, new UserLoginInfo("Google", GoogleKey, "Google"))).Succeeded);
        await using var host = await FromGoogleAsync(new PageHost(app).UseRequest());

        Callback(host, "/tickets");

        Assert.EndsWith("/tickets", host.Nav.Uri);
        Assert.Contains(".AspNetCore.Identity.Application", host.ResponseCookies);
    }

    [Fact]
    public async Task A_verified_google_email_is_linked_to_the_existing_account()
    {
        await using var app = new TestApp();
        var pat = await app.CreateUserAsync("pat@example.com");
        await using var host = await FromGoogleAsync(new PageHost(app).UseRequest());

        Callback(host, "/tickets");

        Assert.EndsWith("/tickets", host.Nav.Uri);
        var logins = await WithUsersAsync(app, async users => await users.GetLoginsAsync((await users.FindByIdAsync(pat.Id))!));
        Assert.Equal(GoogleKey, Assert.Single(logins).ProviderKey);
    }

    [Fact]
    public async Task An_unverified_google_email_isnt_linked_and_is_asked_to_register()
    {
        await using var app = new TestApp();
        await app.CreateUserAsync("pat@example.com");
        await using var host = await FromGoogleAsync(new PageHost(app).UseRequest(), verified: false);

        var page = Callback(host);

        Assert.Contains("Associate your Google account.", page.Text());
        Assert.Equal("pat@example.com", page.Find("[id='Input.Email']").GetAttribute("value"));
    }

    [Fact]
    public async Task An_invitation_takes_over_after_google()
    {
        await using var app = new TestApp();
        await using var host = await FromGoogleAsync(new PageHost(app).UseRequest());

        Callback(host, "/invite/abc");

        Assert.EndsWith("invite/abc?external=1", host.Nav.Uri);
    }

    [Fact]
    public async Task A_new_google_user_registers_and_is_signed_in()
    {
        await using var app = new TestApp();
        await using var host = await FromGoogleAsync(new PageHost(app).UseRequest("POST"), email: "new@example.com");
        var page = host.Render<ExternalLogin>();

        page.Find("[id='Input.Email']").Change("new@example.com"); // as prefilled by the callback
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains(".AspNetCore.Identity.Application", host.ResponseCookies));
        var user = await WithUsersAsync(app, users => users.FindByEmailAsync("new@example.com"));
        Assert.Equal((true, "Pat Google"), (user!.EmailConfirmed, user.DisplayName));
    }

    [Fact]
    public async Task A_new_google_user_with_another_email_confirms_it_first()
    {
        await using var app = new TestApp();
        await using var host = await FromGoogleAsync(new PageHost(app).UseRequest("POST"), email: "new@example.com");
        var page = host.Render<ExternalLogin>();

        page.Find("[id='Input.Email']").Change("other@example.com");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Contains("Account/RegisterConfirmation?email=other%40example.com", host.Nav.Uri));
        Assert.Contains(app.Email.Sent, m => m.To == "other@example.com");
    }

    [Fact]
    public async Task A_provider_error_or_missing_login_goes_back_to_login()
    {
        await using var host = new PageHost().UseRequest();
        host.Nav.NavigateTo("Account/ExternalLogin?RemoteError=denied");
        host.Render<ExternalLogin>();
        Assert.EndsWith("Account/Login", host.Nav.Uri);
        Assert.Contains(IdentityRedirectManager.StatusCookieName, host.ResponseCookies);

        await using var other = new PageHost().UseRequest();
        other.Render<ExternalLogin>();
        Assert.EndsWith("Account/Login", other.Nav.Uri);
    }

    [Fact]
    public async Task Opening_the_page_directly_goes_back_to_login()
    {
        await using var app = new TestApp();
        await using var host = await FromGoogleAsync(new PageHost(app).UseRequest());
        host.Nav.NavigateTo("Account/ExternalLogin");

        host.Render<ExternalLogin>();

        Assert.EndsWith("Account/Login", host.Nav.Uri);
    }

    // --- Linking Google from the account settings ---

    [Fact]
    public async Task A_signed_in_user_links_google_and_sees_it_listed()
    {
        await using var app = new TestApp();
        var pat = await app.CreateUserAsync("pat@example.com");
        await using var host = new PageHost(app).SignIn(pat).UseRequest();
        var properties = new AuthenticationProperties();
        properties.Items["LoginProvider"] = "Google";
        properties.Items["XsrfId"] = pat.Id; // the link flow ties the external cookie to the signed-in user
        await host.CarryCookieAsync(IdentityConstants.ExternalScheme,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, GoogleKey)], "Google")), properties);
        host.Nav.NavigateTo("Account/Manage/ExternalLogins?Action=LinkLoginCallback");

        host.Render<ExternalLogins>();

        Assert.EndsWith("Account/Manage/ExternalLogins", host.Nav.Uri);
        var logins = await WithUsersAsync(app, async users => await users.GetLoginsAsync((await users.FindByIdAsync(pat.Id))!));
        Assert.Equal("Google", Assert.Single(logins).LoginProvider);

        await using var later = new PageHost(app).SignIn(pat).UseRequest();
        var page = later.Render<ExternalLogins>();
        Assert.Contains("Registered Logins", page.Text());
        Assert.Contains("Remove", page.Text());
    }

    [Fact]
    public async Task A_link_callback_without_google_info_says_so()
    {
        await using var app = new TestApp();
        var pat = await app.CreateUserAsync("pat@example.com");
        await using var host = new PageHost(app).SignIn(pat).UseRequest();
        host.Nav.NavigateTo("Account/Manage/ExternalLogins?Action=LinkLoginCallback");

        host.Render<ExternalLogins>();

        Assert.Contains(IdentityRedirectManager.StatusCookieName, host.ResponseCookies);
    }

    [Fact]
    public async Task The_passkeys_page_lists_none_yet()
    {
        await using var app = new TestApp();
        var pat = await app.CreateUserAsync("pat@example.com");
        await using var host = new PageHost(app).SignIn(pat).UseRequest();

        var page = host.Render<Passkeys>();

        Assert.Contains("No passkeys are registered.", page.Text());
    }

    // --- Two-factor sign-in ---

    // A 2FA user who has entered their password: Identity remembers them in the two-factor cookie.
    private static async Task<(ApplicationUser User, string RecoveryCode)> PasswordDoneAsync(PageHost host)
    {
        var pat = await host.App.CreateUserAsync("pat@example.com");
        var codes = await WithUsersAsync(host.App, async users =>
        {
            var user = (await users.FindByIdAsync(pat.Id))!;
            await users.SetTwoFactorEnabledAsync(user, true);
            await users.ResetAuthenticatorKeyAsync(user);
            return await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 2);
        });
        await host.CarryCookieAsync(IdentityConstants.TwoFactorUserIdScheme,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, pat.Id)], IdentityConstants.TwoFactorUserIdScheme)));
        return (pat, codes!.First());
    }

    [Fact]
    public async Task A_recovery_code_signs_in_once()
    {
        await using var host = new PageHost().UseRequest("POST");
        var (pat, code) = await PasswordDoneAsync(host);
        host.Nav.NavigateTo("Account/LoginWithRecoveryCode?ReturnUrl=%2Ftickets");
        var page = host.Render<LoginWithRecoveryCode>();

        page.Find("[id='Input.RecoveryCode']").Change(code);
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.EndsWith("/tickets", host.Nav.Uri));
        Assert.Contains(".AspNetCore.Identity.Application", host.ResponseCookies);
        Assert.Equal(1, await WithUsersAsync(host.App, async users => await users.CountRecoveryCodesAsync((await users.FindByIdAsync(pat.Id))!)));
    }

    [Fact]
    public async Task A_wrong_recovery_code_is_refused()
    {
        await using var host = new PageHost().UseRequest("POST");
        await PasswordDoneAsync(host);
        var page = host.Render<LoginWithRecoveryCode>();

        page.Find("[id='Input.RecoveryCode']").Change("WRONG-CODE");
        page.Find("form").Submit();

        page.WaitForText("Error: Invalid recovery code entered.");
    }

    [Fact]
    public async Task A_wrong_authenticator_code_is_refused()
    {
        await using var host = new PageHost().UseRequest("POST");
        await PasswordDoneAsync(host);
        var page = host.Render<LoginWith2fa>();

        page.Find("[id='Input.TwoFactorCode']").Change("000000");
        page.Find("form").Submit();

        page.WaitForText("Error: Invalid authenticator code.");
    }
}
