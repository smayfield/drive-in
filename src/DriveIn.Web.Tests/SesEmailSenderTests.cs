using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using DriveIn.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DriveIn.Web.Tests;

public class SesEmailSenderTests
{
    // Stands in for SES without network calls; SendEmailAsync is virtual on the real client.
    private sealed class RejectingSesClient()
        : AmazonSimpleEmailServiceV2Client(new BasicAWSCredentials("test", "test"), RegionEndpoint.USEast1)
    {
        public override Task<SendEmailResponse> SendEmailAsync(SendEmailRequest request, CancellationToken ct = default) =>
            throw new MessageRejectedException(
                "Email address is not verified. The following identities failed the check in region US-EAST-1: new@example.com")
            {
                ErrorCode = "MessageRejected",
                StatusCode = HttpStatusCode.BadRequest,
            };
    }

    // Callers log send failures, and SES sandbox rejections name the recipient; that address must not reach the logs.
    [Fact]
    public async Task Rejections_are_rethrown_without_the_recipient()
    {
        var sender = new SesEmailSender(new RejectingSesClient(), Options.Create(new EmailOptions()),
            NullLogger<SesEmailSender>.Instance);

        var ex = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync("new@example.com", "Hi", "<p>Hi</p>"));

        Assert.Contains("MessageRejected", ex.Message);
        Assert.DoesNotContain("new@example.com", ex.ToString());
        Assert.Null(ex.InnerException);
    }
}
