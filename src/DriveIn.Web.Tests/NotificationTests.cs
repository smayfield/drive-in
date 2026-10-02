using System.Diagnostics.Metrics;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using static DriveIn.Web.Tests.MessagingTests;

namespace DriveIn.Web.Tests;

public class NotificationTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMinutes(new NotificationOptions().EmailDelayMinutes);

    private static Task<int> AskAsync(World w, string body = "Secret question about dogs") =>
        w.Messaging.StartWithTheaterAsync(Principals.For(w.Customer), w.Theater.Id, "Dogs", body);

    [Fact]
    public async Task Users_list_and_clear_their_own_notifications()
    {
        await using var w = await SetUpAsync();
        await AskAsync(w);
        var owner = Principals.For(w.Owner);

        var item = Assert.Single(await w.Notifications.ListAsync(owner));
        Assert.False(item.IsRead);
        Assert.Equal(1, await w.Notifications.CountUnreadAsync(owner));
        Assert.Equal(0, await w.Notifications.CountUnreadAsync(Principals.Anonymous));

        // Someone else's notification doesn't exist as far as you're concerned.
        await Assert.ThrowsAsync<NotFoundException>(() => w.Notifications.MarkReadAsync(Principals.For(w.Viewer), item.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Notifications.ListAsync(Principals.Anonymous));

        await w.Notifications.MarkReadAsync(owner, item.Id);
        await w.Notifications.MarkReadAsync(owner, item.Id); // again: no-op
        Assert.True(Assert.Single(await w.Notifications.ListAsync(owner)).IsRead);
        Assert.Equal(0, await w.Notifications.CountUnreadAsync(owner));

        var viewer = Principals.For(w.Viewer);
        await w.Notifications.MarkAllReadAsync(viewer);
        await w.Notifications.MarkAllReadAsync(viewer); // nothing left: no-op
        Assert.Equal(0, await w.Notifications.CountUnreadAsync(viewer));
        Assert.Equal(1, await w.Notifications.CountUnreadAsync(Principals.For(w.Replier)));
    }

    [Fact]
    public async Task Unread_lists_first_newest_first()
    {
        await using var w = await SetUpAsync();
        var first = await AskAsync(w);
        w.App.Time.Advance(TimeSpan.FromMinutes(1));
        await w.Messaging.StartWithTheaterAsync(Principals.For(w.Customer), w.Theater.Id, "Snacks", "Popcorn?");
        var owner = Principals.For(w.Owner);
        await w.Messaging.GetAsync(owner, first); // reads the first

        var items = await w.Notifications.ListAsync(owner);
        Assert.Equal([false, true], items.Select(i => i.IsRead));
    }

    [Fact]
    public async Task Unread_notifications_are_emailed_once_after_the_delay_as_links_only()
    {
        await using var w = await SetUpAsync();
        using var emailed = new MetricCollector<long>(w.App.Get<IMeterFactory>(), DriveInMetrics.MeterName, "drivein.notifications.emailed");
        var id = await AskAsync(w);
        await w.Messaging.GetAsync(Principals.For(w.Viewer), id); // the viewer read it in time

        w.App.Time.Advance(Delay - TimeSpan.FromSeconds(1));
        await w.Notifications.SendDueEmailsAsync();
        Assert.Empty(w.App.Email.Sent);

        w.App.Time.Advance(TimeSpan.FromSeconds(1));
        await w.Notifications.SendDueEmailsAsync();
        Assert.Equal(new[] { "owner@example.com", "replier@example.com" }, w.App.Email.Sent.Select(m => m.To).Order());
        var mail = w.App.Email.Sent.Single(m => m.To == "owner@example.com");
        Assert.Equal("New message from Customer", mail.Subject);
        Assert.Contains($"https://drive-in.online/manage/{w.Theater.Id}/messages/{id}", mail.Body);
        Assert.Contains("https://drive-in.online/Account/Manage/Notifications", mail.Body);
        Assert.DoesNotContain("Secret question", mail.Body);
        Assert.DoesNotContain("Dogs", mail.Body); // nor the subject
        Assert.Equal(2, emailed.GetMeasurementSnapshot().Count);

        // Not again, even when more messages arrive before they read it. (The viewer, who had read it, is told anew.)
        await w.Messaging.PostAsync(Principals.For(w.Customer), id, "Hello?");
        w.App.Time.Advance(Delay);
        await w.Notifications.SendDueEmailsAsync();
        Assert.Single(w.App.Email.Sent, m => m.To == "owner@example.com");
        Assert.Single(w.App.Email.Sent, m => m.To == "replier@example.com");
        Assert.Single(w.App.Email.Sent, m => m.To == "viewer@example.com");
    }

    [Fact]
    public async Task One_email_lists_all_of_a_users_due_notifications()
    {
        await using var w = await SetUpAsync();
        await AskAsync(w);
        await w.Messaging.StartWithTheaterAsync(Principals.For(w.Customer), w.Theater.Id, "Snacks", "Popcorn?");

        w.App.Time.Advance(Delay);
        await w.Notifications.SendDueEmailsAsync();

        var mail = Assert.Single(w.App.Email.Sent, m => m.To == "owner@example.com");
        Assert.Equal("You have 2 new notifications on Drive-In Online", mail.Subject);
    }

    [Fact]
    public async Task A_new_message_after_reading_is_emailed_again()
    {
        await using var w = await SetUpAsync();
        var id = await AskAsync(w);
        w.App.Time.Advance(Delay);
        await w.Notifications.SendDueEmailsAsync();
        await w.Messaging.GetAsync(Principals.For(w.Owner), id);

        await w.Messaging.PostAsync(Principals.For(w.Customer), id, "Hello?");
        w.App.Time.Advance(Delay);
        w.App.Email.Sent.Clear();
        await w.Notifications.SendDueEmailsAsync();

        Assert.Contains(w.App.Email.Sent, m => m.To == "owner@example.com");
    }

    [Fact]
    public async Task People_who_turned_email_off_or_havent_confirmed_aren_t_emailed()
    {
        await using var w = await SetUpAsync();
        await using (var db = w.App.Db())
        {
            (await db.Users.SingleAsync(u => u.Id == w.Owner.Id)).EmailNotifications = false;
            (await db.Users.SingleAsync(u => u.Id == w.Viewer.Id)).EmailConfirmed = false;
            await db.SaveChangesAsync();
        }
        await AskAsync(w);

        w.App.Time.Advance(Delay);
        await w.Notifications.SendDueEmailsAsync();

        Assert.Equal(["replier@example.com"], w.App.Email.Sent.Select(m => m.To));
    }

    [Fact]
    public async Task A_failed_send_is_retried_and_old_notifications_are_dropped()
    {
        await using var w = await SetUpAsync();
        await AskAsync(w);
        w.App.Time.Advance(Delay);
        w.App.Email.FailWith = new EmailSendException("SES is down");

        await w.Notifications.SendDueEmailsAsync();
        Assert.Empty(w.App.Email.Sent);

        w.App.Email.FailWith = null;
        await w.Notifications.SendDueEmailsAsync();
        Assert.Equal(3, w.App.Email.Sent.Count);

        // A notification that's been waiting longer than the maximum age isn't emailed at all.
        await AskAsync(w);
        w.App.Time.Advance(TimeSpan.FromHours(new NotificationOptions().EmailMaxAgeHours) + TimeSpan.FromMinutes(1));
        w.App.Email.Sent.Clear();
        await w.Notifications.SendDueEmailsAsync();
        Assert.Empty(w.App.Email.Sent);
    }

    [Fact]
    public async Task The_background_job_sends_due_emails()
    {
        await using var w = await SetUpAsync();
        await AskAsync(w);
        w.App.Time.Advance(Delay);
        using var job = new NotificationEmailService(
            w.App.Get<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(), w.App.Time, w.App.Get<DriveInMetrics>(),
            AlwaysLeader.Instance, Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationEmailService>.Instance);

        await job.StartAsync(CancellationToken.None);
        for (var i = 0; i < 50 && w.App.Email.Sent.Count < 3; i++)
        {
            w.App.Time.Advance(NotificationEmailService.Interval);
            await Task.Delay(20);
        }
        await job.StopAsync(CancellationToken.None);

        Assert.Equal(3, w.App.Email.Sent.Count);
    }

    [Fact]
    public void Message_titles_count_and_stay_within_the_column()
    {
        Assert.Equal("New message from Pat", NotificationService.MessageTitle(1, "Pat"));
        Assert.Equal("4 new messages from Pat", NotificationService.MessageTitle(4, "Pat"));
        // Titles become email subjects: a display name can't break onto new lines.
        Assert.Equal("New message from Pat Bcc: x", NotificationService.MessageTitle(1, "Pat\r\nBcc: x"));
        var longTitle = NotificationService.MessageTitle(1, new string('x', 500));
        Assert.Equal(Notification.MaxTitleLength, longTitle.Length);
        Assert.EndsWith("…", longTitle);
    }
}
