using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Identity;

namespace DriveIn.Web.Services;

// An image shown in the body with <img src="cid:{ContentId}">. Inline attachments display in mail clients that
// block data: URIs and remote images.
public sealed record InlineImage(string ContentId, string FileName, string ContentType, byte[] Content);

public interface IAppEmailSender
{
    Task SendAsync(string to, string subject, string htmlBody, IReadOnlyList<InlineImage>? images = null, CancellationToken ct = default);
}

public sealed class EmailOptions
{
    public const string Section = "Email";

    // "Ses" in production; anything else logs messages instead of sending them.
    public string Provider { get; set; } = "Log";
    public string From { get; set; } = "Drive-In Online <no-reply@drive-in.online>";
}

public sealed class SesEmailSender(IAmazonSimpleEmailServiceV2 ses, Microsoft.Extensions.Options.IOptions<EmailOptions> options, ILogger<SesEmailSender> logger)
    : IAppEmailSender
{
    public async Task SendAsync(string to, string subject, string htmlBody, IReadOnlyList<InlineImage>? images = null,
        CancellationToken ct = default)
    {
        try
        {
            await SendCoreAsync(to, subject, htmlBody, images, ct);
        }
        catch (Amazon.Runtime.AmazonServiceException ex)
        {
            // SES error messages can name the recipient (e.g. a sandbox rejection lists the unverified address), and
            // callers log what we throw, so pass on only the error code and AWS request ID and drop the original exception.
            throw new EmailSendException(
                $"SES couldn't send the email: {ex.ErrorCode} (HTTP {(int)ex.StatusCode}, AWS request {ex.RequestId}).");
        }
        // The recipient isn't logged: email addresses don't belong in production logs.
        logger.LogInformation("Sent email {Subject}", subject);
    }

    private Task SendCoreAsync(string to, string subject, string htmlBody, IReadOnlyList<InlineImage>? images, CancellationToken ct) =>
        ses.SendEmailAsync(new SendEmailRequest
        {
            FromEmailAddress = options.Value.From,
            Destination = new Destination { ToAddresses = [to] },
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Content { Data = subject, Charset = "UTF-8" },
                    Body = new Body { Html = new Content { Data = htmlBody, Charset = "UTF-8" } },
                    Attachments = images?.Select(i => new Attachment
                    {
                        FileName = i.FileName,
                        ContentType = i.ContentType,
                        ContentId = i.ContentId,
                        ContentDisposition = AttachmentContentDisposition.INLINE,
                        RawContent = new MemoryStream(i.Content),
                    }).ToList(),
                },
            },
        }, ct);
}

// A failed send, with a message safe to log (no recipient address).
public sealed class EmailSendException(string message) : Exception(message);

// Development: writes the email (and any links in it) to the log so flows can be tested without SES. Logging the
// recipient and body is the point (it's how a developer gets confirmation, invite and ticket links locally), so the
// CodeQL "exposure of private information" alert here is deliberate. Production sets Email:Provider=Ses.
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IAppEmailSender
{
    public Task SendAsync(string to, string subject, string htmlBody, IReadOnlyList<InlineImage>? images = null,
        CancellationToken ct = default)
    {
        logger.LogWarning("EMAIL to {To}: {Subject} ({Images} inline images)\n{Body}", to, subject, images?.Count ?? 0, htmlBody);
        return Task.CompletedTask;
    }
}

public sealed record AccountEmail(string Subject, string Html);

// The account emails' content. Links arrive already HTML-encoded.
public static class AccountEmails
{
    public static AccountEmail ConfirmationLink(string link) => new("Confirm your Drive-In Online account",
        $"<p>Confirm your account by <a href=\"{link}\">clicking here</a>.</p>");

    public static AccountEmail PasswordResetLink(string link) => new("Reset your Drive-In Online password",
        $"<p>Reset your password by <a href=\"{link}\">clicking here</a>. If you didn't ask for this, you can ignore this email.</p>");

    public static AccountEmail PasswordResetCode(string code) => new("Reset your Drive-In Online password",
        $"<p>Reset your password using this code: {System.Net.WebUtility.HtmlEncode(code)}</p>");
}

public static class EmailSenderExtensions
{
    // For actions whose caller must know whether the email went out (an admin sending a reset link): a failed send is
    // logged and reported as a validation error, which pages show as an alert.
    public static async Task SendOrReportAsync(this IAppEmailSender sender, ILogger logger, string to, AccountEmail email,
        string failureMessage)
    {
        try
        {
            await sender.SendAsync(to, email.Subject, email.Html);
        }
        catch (Exception ex)
        {
            // The recipient isn't logged: email addresses don't belong in production logs.
            logger.LogError(ex, "Couldn't send email {Subject}", email.Subject);
            throw new AppValidationException(failureMessage);
        }
    }
}

// Adapts the Identity UI's email callbacks, for the self-service account pages only (register, resend confirmation,
// forgot password, change email). A failed send is logged, not thrown: by then the account (or reset request) exists
// and the pages offer a resend, so an SES rejection or outage shouldn't turn registration into an error page. Services
// that act for someone else use IAppEmailSender.SendOrReportAsync instead, so the actor hears about a failure.
public sealed class IdentityEmailSender(IAppEmailSender sender, ILogger<IdentityEmailSender> logger) : IEmailSender<ApplicationUser>
{
    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        TrySendAsync(email, AccountEmails.ConfirmationLink(confirmationLink));

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        TrySendAsync(email, AccountEmails.PasswordResetLink(resetLink));

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        TrySendAsync(email, AccountEmails.PasswordResetCode(resetCode));

    private async Task TrySendAsync(string to, AccountEmail email)
    {
        try
        {
            await sender.SendAsync(to, email.Subject, email.Html);
        }
        // Catches cancellation too, deliberately: no request token is passed in, so a cancellation here is an SDK
        // timeout, which is just another failed send. SesEmailSender strips recipients from SES errors before they
        // get here.
        catch (Exception ex)
        {
            // The recipient isn't logged: email addresses don't belong in production logs.
            logger.LogError(ex, "Couldn't send email {Subject}", email.Subject);
        }
    }
}
