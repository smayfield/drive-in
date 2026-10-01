using System.Net;
using System.Security.Claims;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DriveIn.Web.Services;

public sealed class NotificationOptions
{
    public const string Section = "Notifications";

    // How long a notification stays unread before it's emailed. Someone using the app sees it first and isn't emailed.
    public int EmailDelayMinutes { get; set; } = 10;

    // Notifications older than this aren't emailed (e.g. after someone turns email back on, or an address that keeps
    // failing): the email is a nudge, not a backlog.
    public int EmailMaxAgeHours { get; set; } = 48;

    // The site's address, for links in emails sent by the background job (which has no request to take it from).
    public string SiteUrl { get; set; } = "https://drive-in.online/";
}

public sealed record NotificationItem(long Id, NotificationKind Kind, string Title, string Link, int Count,
    DateTimeOffset UpdatedAt, bool IsRead);

// Tells the signed-in user's open pages (the bell) that their notifications changed. In-process, like SpotEvents.
public sealed class NotificationEvents
{
    public event Action<string>? Changed;

    public void Publish(string userId)
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<string>)handler)(userId);
            }
            catch
            {
                // One broken subscriber (e.g. a closing circuit) mustn't stop the others hearing about it.
            }
        }
    }
}

// The bell: notifications for one user, read in the app and emailed (as links only) if they're still unread after a
// while. Other services create them (after their own permission checks); users only see and clear their own.
public sealed class NotificationService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    TimeProvider time,
    NotificationEvents events,
    IAppEmailSender email,
    IOptions<NotificationOptions> options,
    DriveInMetrics metrics,
    ILogger<NotificationService> logger)
{
    public const int BellSize = 10;

    // --- Raised by other services ---

    // A new message in a conversation, for each recipient. Each has at most one unread notification per conversation:
    // more messages before they read it update its count (and don't send another email).
    public async Task NotifyMessageAsync(IEnumerable<string> userIds, int conversationId, string from, string link)
    {
        var recipients = userIds.Distinct().ToList();
        if (recipients.Count == 0)
            return;
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync();
        var unread = await db.Notifications
            .Where(n => n.ConversationId == conversationId && n.ReadAt == null && recipients.Contains(n.UserId))
            .ToListAsync();
        foreach (var userId in recipients)
        {
            var existing = unread.FirstOrDefault(n => n.UserId == userId);
            if (existing is null)
            {
                db.Notifications.Add(new Notification
                {
                    UserId = userId,
                    Kind = NotificationKind.Message,
                    ConversationId = conversationId,
                    Link = link,
                    Title = MessageTitle(1, from),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else
            {
                existing.Count++;
                existing.Title = MessageTitle(existing.Count, from);
                existing.UpdatedAt = now;
            }
        }
        await db.SaveChangesAsync();
        foreach (var userId in recipients)
            events.Publish(userId);
    }

    // Opening a conversation reads its notifications.
    public async Task MarkConversationReadAsync(string userId, int conversationId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var unread = await db.Notifications
            .Where(n => n.UserId == userId && n.ConversationId == conversationId && n.ReadAt == null)
            .ToListAsync();
        if (unread.Count == 0)
            return;
        var now = time.GetUtcNow();
        unread.ForEach(n => n.ReadAt = now);
        await db.SaveChangesAsync();
        events.Publish(userId);
    }

    internal static string MessageTitle(int count, string from) =>
        Truncate(count == 1 ? $"New message from {from}" : $"{count} new messages from {from}", Notification.MaxTitleLength);

    // --- The signed-in user's own ---

    public async Task<List<NotificationItem>> ListAsync(ClaimsPrincipal user, int take = 100)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Notifications.AsNoTracking()
            .Where(n => n.UserId == userId)
            .OrderBy(n => n.ReadAt != null).ThenByDescending(n => n.UpdatedAt)
            .Take(take)
            .Select(n => new NotificationItem(n.Id, n.Kind, n.Title, n.Link, n.Count, n.UpdatedAt, n.ReadAt != null))
            .ToListAsync();
    }

    public async Task<int> CountUnreadAsync(ClaimsPrincipal user)
    {
        if (user.GetUserId() is not string userId)
            return 0;
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null);
    }

    public async Task MarkReadAsync(ClaimsPrincipal user, long id)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        // Someone else's notification is "not found" rather than "denied", so ids don't confirm what exists.
        var notification = await db.Notifications.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId)
            ?? throw new NotFoundException("Notification not found.");
        if (notification.ReadAt is not null)
            return;
        notification.ReadAt = time.GetUtcNow();
        await db.SaveChangesAsync();
        events.Publish(userId);
    }

    public async Task MarkAllReadAsync(ClaimsPrincipal user)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var unread = await db.Notifications.Where(n => n.UserId == userId && n.ReadAt == null).ToListAsync();
        if (unread.Count == 0)
            return;
        var now = time.GetUtcNow();
        unread.ForEach(n => n.ReadAt = now);
        await db.SaveChangesAsync();
        events.Publish(userId);
    }

    // --- Email (NotificationEmailService) ---

    // Emails each user who has notifications still unread after the delay (and not yet emailed) one digest of links.
    // Users who turned email off, or whose address isn't confirmed, aren't emailed. A failed send is retried next run.
    public async Task SendDueEmailsAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var dueBefore = now.AddMinutes(-options.Value.EmailDelayMinutes);
        var notAfter = now.AddHours(-options.Value.EmailMaxAgeHours);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var due = await db.Notifications
            .Where(n => n.ReadAt == null && n.EmailedAt == null && n.UpdatedAt <= dueBefore && n.UpdatedAt > notAfter
                && n.User!.EmailNotifications && n.User.EmailConfirmed && n.User.Email != null)
            .Select(n => new { Notification = n, n.User!.Email })
            .ToListAsync(ct);
        var siteUrl = options.Value.SiteUrl;
        foreach (var group in due.GroupBy(d => d.Email!))
        {
            var notifications = group.Select(d => d.Notification).OrderByDescending(n => n.UpdatedAt).ToList();
            var digest = NotificationEmails.Digest(notifications, siteUrl);
            try
            {
                await email.SendAsync(group.Key, digest.Subject, digest.Html, ct: ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // The recipient isn't logged: email addresses don't belong in production logs.
                logger.LogError(ex, "Couldn't send email {Subject}", digest.Subject);
                continue;
            }
            notifications.ForEach(n => n.EmailedAt = now);
            await db.SaveChangesAsync(ct);
            metrics.NotificationEmailed();
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

// The notification email: titles and links only. Messages themselves are never emailed; they stay in the app.
public static class NotificationEmails
{
    public static AccountEmail Digest(IReadOnlyList<Notification> notifications, string siteUrl)
    {
        var site = siteUrl.TrimEnd('/') + "/";
        var subject = notifications.Count == 1
            ? notifications[0].Title
            : $"You have {notifications.Count} new notifications on Drive-In Online";
        var items = string.Concat(notifications.Select(n =>
            $"<li><a href=\"{WebUtility.HtmlEncode(site + n.Link)}\">{WebUtility.HtmlEncode(n.Title)}</a></li>"));
        var settings = WebUtility.HtmlEncode(site + "Account/Manage/Notifications");
        return new AccountEmail(subject,
            $"<p>You have something new on Drive-In Online:</p><ul>{items}</ul>" +
            "<p>Sign in to read and reply.</p>" +
            $"<p style=\"color:#666;font-size:small\">To stop these emails, turn them off in your <a href=\"{settings}\">notification settings</a>.</p>");
    }
}

// Emails unread notifications every minute (NotificationService.SendDueEmailsAsync).
public sealed class NotificationEmailService(IServiceScopeFactory scopes, TimeProvider time, DriveInMetrics metrics,
    ILogger<NotificationEmailService> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<NotificationService>().SendDueEmailsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Couldn't email notifications");
                metrics.JobFailed("notification_email");
            }
        }
    }
}
