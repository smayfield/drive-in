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
        await ses.SendEmailAsync(new SendEmailRequest
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
        logger.LogInformation("Sent email {Subject} to {To}", subject, to);
    }
}

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

// Adapts the Identity UI's email callbacks. Links arrive already HTML-encoded.
public sealed class IdentityEmailSender(IAppEmailSender sender) : IEmailSender<ApplicationUser>
{
    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        sender.SendAsync(email, "Confirm your Drive-In Online account",
            $"<p>Confirm your account by <a href=\"{confirmationLink}\">clicking here</a>.</p>");

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        sender.SendAsync(email, "Reset your Drive-In Online password",
            $"<p>Reset your password by <a href=\"{resetLink}\">clicking here</a>. If you didn't ask for this, you can ignore this email.</p>");

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        sender.SendAsync(email, "Reset your Drive-In Online password",
            $"<p>Reset your password using this code: {System.Net.WebUtility.HtmlEncode(resetCode)}</p>");
}
