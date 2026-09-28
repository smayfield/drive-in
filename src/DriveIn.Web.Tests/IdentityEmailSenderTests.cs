using DriveIn.Web.Data;
using Microsoft.AspNetCore.Identity;

namespace DriveIn.Web.Tests;

public class IdentityEmailSenderTests
{
    [Fact]
    public async Task Sends_confirmation_link()
    {
        await using var app = new TestApp();
        var sender = app.Get<IEmailSender<ApplicationUser>>();

        await sender.SendConfirmationLinkAsync(new ApplicationUser(), "new@example.com", "https://drive-in.test/confirm");

        var sent = Assert.Single(app.Email.Sent);
        Assert.Equal("new@example.com", sent.To);
        Assert.Contains("https://drive-in.test/confirm", sent.Body);
    }

    // Registration creates the account before sending; a rejected send (SES sandbox, outage) must not
    // surface as an error page, since the user can resend the confirmation later.
    [Fact]
    public async Task Failed_sends_are_logged_not_thrown()
    {
        await using var app = new TestApp();
        app.Email.FailWith = new InvalidOperationException("Email address is not verified.");
        var sender = app.Get<IEmailSender<ApplicationUser>>();
        var user = new ApplicationUser();

        await sender.SendConfirmationLinkAsync(user, "new@example.com", "https://drive-in.test/confirm");
        await sender.SendPasswordResetLinkAsync(user, "new@example.com", "https://drive-in.test/reset");
        await sender.SendPasswordResetCodeAsync(user, "new@example.com", "123456");

        Assert.Empty(app.Email.Sent);
    }
}
