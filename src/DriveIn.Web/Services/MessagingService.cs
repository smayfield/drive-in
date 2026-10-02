using System.Security.Claims;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Services;

public sealed record ConversationSummary(
    int Id, ConversationKind Kind, string Subject, int? TheaterId, string? TheaterName, string CustomerName,
    DateTimeOffset LastMessageAt, bool IsClosed, bool Unread, string Preview);

public sealed record MessageView(long Id, MessageSide Side, string SenderName, bool Mine, string Body, DateTimeOffset SentAt);

public sealed record ConversationView(
    int Id, ConversationKind Kind, string Subject, int? TheaterId, string? TheaterName, string? TheaterSlug,
    int? AboutTheaterId, string? AboutTheaterName, string CustomerName, string? CustomerEmail, DateTimeOffset CreatedAt,
    bool IsClosed, bool IsCustomer, bool CanReply, bool CanClose, List<MessageView> Messages);

// Tells open conversation pages that a conversation changed (a new message, closed or reopened). In-process, like
// SpotEvents.
public sealed class MessageEvents
{
    public event Action<int>? Changed;

    public void Publish(int conversationId)
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<int>)handler)(conversationId);
            }
            catch
            {
                // One broken subscriber (e.g. a closing circuit) mustn't stop the others hearing about it.
            }
        }
    }
}

// In-app conversations, stored in the database (never emailed; recipients get a notification instead):
// - Theater: a signed-in customer and a theater they can browse. Staff read with messages.view and reply, close and
//   reopen with messages.reply; the owner has both. Admins can read every one but don't post in them.
// - Support: anyone signed in (typically a current or future owner) and the site admins, optionally about one of the
//   person's theaters. Admins read, reply, close and reopen.
// Who someone is in a conversation (its customer, the theater's staff, or support) decides what they see and can do.
public sealed class MessagingService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    TheaterAccess access,
    TimeProvider time,
    NotificationService notifications,
    MessageEvents events,
    DriveInMetrics metrics,
    ActionRateLimiter limiter)
{
    public const int MaxNewConversationsPerDay = 10;
    public const string SupportName = "Drive-In Online support";
    public const string DeletedAccount = "Deleted account";
    private const int PreviewLength = 120;

    // --- Starting a conversation ---

    public async Task<int> StartWithTheaterAsync(ClaimsPrincipal user, int theaterId, string subject, string body)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Id == theaterId);
        // Demo and inactive theaters are private: to outsiders they don't exist.
        if (theater is null || !TheaterService.CanBrowse(user, theater))
            throw new NotFoundException("Theater not found.");
        if (IsStaff(user, theater))
            throw new AppValidationException("You can't message a theater you work for.");
        var conversation = await StartAsync(db, userId, ConversationKind.Theater, theaterId, aboutTheaterId: null, subject, body);

        var customer = await CustomerNameAsync(db, userId);
        await notifications.NotifyMessageAsync(await StaffRecipientsAsync(db, theater, except: userId), conversation.Id,
            customer, StaffLink(conversation));
        Done(conversation, MessageSide.Customer);
        return conversation.Id;
    }

    // Anyone signed in can contact support; the theater it's about (if any) must be one of theirs.
    public async Task<int> StartSupportAsync(ClaimsPrincipal user, int? aboutTheaterId, string subject, string body)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        if (aboutTheaterId is int id)
        {
            var theater = await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id)
                ?? throw new NotFoundException("Theater not found.");
            if (!TheaterAccess.IsMember(user, theater))
                throw new AccessDeniedException();
        }
        var conversation = await StartAsync(db, userId, ConversationKind.Support, theaterId: null, aboutTheaterId, subject, body);

        var customer = await CustomerNameAsync(db, userId);
        await notifications.NotifyMessageAsync(await AdminIdsAsync(db, except: userId), conversation.Id, customer,
            AdminLink(conversation));
        Done(conversation, MessageSide.Customer);
        return conversation.Id;
    }

    private async Task<Conversation> StartAsync(ApplicationDbContext db, string userId, ConversationKind kind,
        int? theaterId, int? aboutTheaterId, string subject, string body)
    {
        var cleanSubject = CleanSubject(subject);
        var cleanBody = CleanBody(body);
        var now = time.GetUtcNow();
        var since = now.AddDays(-1);
        if (await db.Conversations.CountAsync(c => c.CustomerId == userId && c.CreatedAt > since) >= MaxNewConversationsPerDay)
            throw new AppValidationException(
                $"You can start up to {MaxNewConversationsPerDay} conversations a day. Please reply in one you've already started, or try again tomorrow.");
        // After the daily cap, whose message says more; this one stops bursts (and replies, in PostAsync).
        limiter.Hit(RateLimitPolicies.Messages, ActionRateLimiter.KeyForUser(userId));

        var conversation = new Conversation
        {
            Kind = kind,
            TheaterId = theaterId,
            AboutTheaterId = aboutTheaterId,
            CustomerId = userId,
            Subject = cleanSubject,
            CreatedAt = now,
            LastMessageAt = now,
            Messages = [new Message { SenderId = userId, Side = MessageSide.Customer, Body = cleanBody, SentAt = now }],
        };
        db.Conversations.Add(conversation);
        db.ConversationReads.Add(new ConversationRead { Conversation = conversation, UserId = userId, ReadAt = now });
        await db.SaveChangesAsync();
        return conversation;
    }

    // --- Lists ---

    // The conversations this person started: with theaters, and with support.
    public async Task<List<ConversationSummary>> ListMineAsync(ClaimsPrincipal user)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        return await SummariesAsync(db, db.Conversations.Where(c => c.CustomerId == userId), userId, includeEmail: false);
    }

    // A theater's inbox: messages.view, or an admin (who can read but not reply).
    public async Task<List<ConversationSummary>> ListForTheaterAsync(ClaimsPrincipal user, int theaterId, bool closed = false)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var theater = await db.Theaters.AsNoTracking().FirstOrDefaultAsync(t => t.Id == theaterId)
            ?? throw new NotFoundException("Theater not found.");
        if (!user.IsAdmin() && !(IsStaff(user, theater) && await CanAsync(user, theater, TheaterPermissions.ViewMessages)))
            throw new AccessDeniedException();
        var query = db.Conversations.Where(c => c.Kind == ConversationKind.Theater && c.TheaterId == theaterId
            && (c.ClosedAt != null) == closed);
        return await SummariesAsync(db, query, userId, includeEmail: user.IsAdmin());
    }

    // Admin: the support inbox.
    public async Task<List<ConversationSummary>> ListSupportAsync(ClaimsPrincipal user, bool closed = false)
    {
        Guard.RequireAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.Conversations.Where(c => c.Kind == ConversationKind.Support && (c.ClosedAt != null) == closed);
        return await SummariesAsync(db, query, user.GetUserId()!, includeEmail: true);
    }

    // Admin: every theater's conversations with customers, or one theater's, open and closed.
    public async Task<List<ConversationSummary>> ListTheaterConversationsAsync(ClaimsPrincipal user, int? theaterId = null)
    {
        Guard.RequireAdmin(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.Conversations.Where(c => c.Kind == ConversationKind.Theater);
        if (theaterId is int id)
            query = query.Where(c => c.TheaterId == id);
        return await SummariesAsync(db, query, user.GetUserId()!, includeEmail: true);
    }

    private static async Task<List<ConversationSummary>> SummariesAsync(ApplicationDbContext db,
        IQueryable<Conversation> query, string userId, bool includeEmail)
    {
        var rows = await query.AsNoTracking()
            .OrderByDescending(c => c.LastMessageAt)
            .Take(200)
            .Select(c => new
            {
                c.Id, c.Kind, c.Subject, c.TheaterId, c.LastMessageAt, c.ClosedAt,
                TheaterName = c.Theater != null ? c.Theater.Name : c.AboutTheater != null ? c.AboutTheater.Name : null,
                CustomerName = c.Customer != null ? c.Customer.DisplayName : null,
                CustomerEmail = c.Customer != null ? c.Customer.Email : null,
                HasCustomer = c.Customer != null,
                Unread = !db.ConversationReads.Any(r => r.ConversationId == c.Id && r.UserId == userId && r.ReadAt >= c.LastMessageAt),
                Last = c.Messages.OrderByDescending(m => m.SentAt).ThenByDescending(m => m.Id).Select(m => m.Body).FirstOrDefault(),
            })
            .ToListAsync();
        return rows.Select(r => new ConversationSummary(r.Id, r.Kind, r.Subject, r.TheaterId, r.TheaterName,
                CustomerLabel(r.HasCustomer, r.CustomerName, includeEmail ? r.CustomerEmail : null),
                r.LastMessageAt, r.ClosedAt is not null, r.Unread, Preview(r.Last)))
            .ToList();
    }

    // --- One conversation ---

    // The conversation as this person may see it, marked read for them.
    public async Task<ConversationView> GetAsync(ClaimsPrincipal user, int conversationId)
    {
        var userId = Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var conversation = await db.Conversations.AsNoTracking()
            .Include(c => c.Theater).Include(c => c.AboutTheater).Include(c => c.Customer)
            .Include(c => c.Messages).ThenInclude(m => m.Sender)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.Id == conversationId)
            ?? throw new NotFoundException("Conversation not found.");
        var role = await RoleAsync(user, conversation);
        if (role.Side is null && !role.CanRead)
            throw new AccessDeniedException();

        await MarkReadAsync(db, conversationId, userId);
        await notifications.MarkConversationReadAsync(userId, conversationId);

        var isCustomer = role.Side == MessageSide.Customer;
        // Theater staff and admins see who on staff wrote a reply; the customer sees the theater.
        var insider = !isCustomer;
        var admin = user.IsAdmin();
        var messages = conversation.Messages
            .OrderBy(m => m.SentAt).ThenBy(m => m.Id)
            .Select(m => new MessageView(m.Id, m.Side, SenderName(conversation, m, insider, admin), m.SenderId == userId,
                m.Body, m.SentAt))
            .ToList();
        return new ConversationView(conversation.Id, conversation.Kind, conversation.Subject, conversation.TheaterId,
            conversation.Theater?.Name, conversation.Theater?.Slug, conversation.AboutTheaterId, conversation.AboutTheater?.Name,
            CustomerLabel(conversation.Customer is not null, conversation.Customer?.DisplayName, admin ? conversation.Customer?.Email : null),
            admin ? conversation.Customer?.Email : null, conversation.CreatedAt, conversation.IsClosed, isCustomer,
            CanReply: role.Side is not null && !conversation.IsClosed,
            CanClose: role.Side is MessageSide.Theater or MessageSide.Support, messages);
    }

    public async Task PostAsync(ClaimsPrincipal user, int conversationId, string body)
    {
        var userId = Guard.RequireUserId(user);
        var clean = CleanBody(body);
        await using var db = await dbFactory.CreateDbContextAsync();
        var conversation = await db.Conversations.Include(c => c.Theater)
            .FirstOrDefaultAsync(c => c.Id == conversationId)
            ?? throw new NotFoundException("Conversation not found.");
        var role = await RoleAsync(user, conversation);
        if (role.Side is not MessageSide side)
            throw new AccessDeniedException();
        if (conversation.IsClosed)
            throw new AppValidationException("This conversation is closed.");
        limiter.Hit(RateLimitPolicies.Messages, ActionRateLimiter.KeyForUser(userId));

        var now = time.GetUtcNow();
        db.Messages.Add(new Message { ConversationId = conversationId, SenderId = userId, Side = side, Body = clean, SentAt = now });
        conversation.LastMessageAt = now;
        await db.SaveChangesAsync();
        await MarkReadAsync(db, conversationId, userId);

        if (side == MessageSide.Customer)
        {
            var customer = await CustomerNameAsync(db, userId);
            if (conversation.Kind == ConversationKind.Theater)
                await notifications.NotifyMessageAsync(await StaffRecipientsAsync(db, conversation.Theater!, except: userId),
                    conversationId, customer, StaffLink(conversation));
            else
                await notifications.NotifyMessageAsync(await AdminIdsAsync(db, except: userId), conversationId, customer,
                    AdminLink(conversation));
        }
        else if (conversation.CustomerId is string customerId && customerId != userId)
        {
            var from = side == MessageSide.Support ? SupportName : conversation.Theater!.Name;
            await notifications.NotifyMessageAsync([customerId], conversationId, from, CustomerLink(conversation));
        }
        Done(conversation, side);
    }

    // The theater (messages.reply) or, for support, an admin closes a conversation once it's dealt with, or reopens it.
    public async Task SetClosedAsync(ClaimsPrincipal user, int conversationId, bool closed)
    {
        Guard.RequireUserId(user);
        await using var db = await dbFactory.CreateDbContextAsync();
        var conversation = await db.Conversations.Include(c => c.Theater)
            .FirstOrDefaultAsync(c => c.Id == conversationId)
            ?? throw new NotFoundException("Conversation not found.");
        var role = await RoleAsync(user, conversation);
        if (role.Side is null or MessageSide.Customer)
            throw new AccessDeniedException();
        conversation.ClosedAt = closed ? conversation.ClosedAt ?? time.GetUtcNow() : null;
        await db.SaveChangesAsync();
        events.Publish(conversationId);
    }

    // --- Who's who ---

    // Side: the side this person writes as (null if they can't post). CanRead: they may read it.
    private sealed record Role(MessageSide? Side, bool CanRead);

    private async Task<Role> RoleAsync(ClaimsPrincipal user, Conversation conversation)
    {
        var userId = user.GetUserId();
        if (userId is not null && conversation.CustomerId == userId)
            return new(MessageSide.Customer, true);
        if (conversation.Kind == ConversationKind.Support)
            return user.IsAdmin() ? new(MessageSide.Support, true) : new(null, false);

        var theater = conversation.Theater ?? throw new NotFoundException("Conversation not found.");
        if (IsStaff(user, theater))
        {
            var permissions = await access.GetPermissionsAsync(user, theater);
            if (permissions.Contains(TheaterPermissions.ViewMessages))
                return new(permissions.Contains(TheaterPermissions.ReplyMessages) ? MessageSide.Theater : null, true);
        }
        // Admins oversee theater conversations but don't take part in them.
        return new(null, user.IsAdmin());
    }

    // The theater's owner or one of its employees: the people who answer its messages. Admins are not, even though
    // they pass every theater permission check.
    private static bool IsStaff(ClaimsPrincipal user, Theater theater) =>
        user.GetUserId() is string userId && (theater.OwnerId == userId || user.GetEmployeeTheaterId() == theater.Id);

    private async Task<bool> CanAsync(ClaimsPrincipal user, Theater theater, string permission) =>
        (await access.GetPermissionsAsync(user, theater)).Contains(permission);

    // Who's told about a customer's message: the owner, and employees whose roles grant messages.view.
    private static async Task<List<string>> StaffRecipientsAsync(ApplicationDbContext db, Theater theater, string except)
    {
        var employees = await db.EmployeeRoles
            .Where(m => m.Role!.TheaterId == theater.Id && m.User!.EmployeeTheaterId == theater.Id
                && m.Role.Permissions.Any(p => p.Permission == TheaterPermissions.ViewMessages))
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync();
        if (theater.OwnerId is string ownerId)
            employees.Add(ownerId);
        return employees.Where(id => id != except).Distinct().ToList();
    }

    private static async Task<List<string>> AdminIdsAsync(ApplicationDbContext db, string except) =>
        await db.UserRoles
            .Where(ur => db.Roles.Any(r => r.Id == ur.RoleId && r.Name == Roles.Admin) && ur.UserId != except)
            .Select(ur => ur.UserId)
            .ToListAsync();

    private static async Task<string> CustomerNameAsync(ApplicationDbContext db, string userId) =>
        CustomerLabel(true, await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).FirstOrDefaultAsync(), null);

    private async Task MarkReadAsync(ApplicationDbContext db, int conversationId, string userId)
    {
        var now = time.GetUtcNow();
        var read = await db.ConversationReads.FirstOrDefaultAsync(r => r.ConversationId == conversationId && r.UserId == userId);
        if (read is not null)
        {
            read.ReadAt = now;
            await db.SaveChangesAsync();
            return;
        }
        var added = db.ConversationReads.Add(new ConversationRead { ConversationId = conversationId, UserId = userId, ReadAt = now });
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // The same person opened it twice at once (two tabs): the other request added the row, so update it instead.
            added.State = EntityState.Detached;
            await db.ConversationReads.Where(r => r.ConversationId == conversationId && r.UserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReadAt, now));
        }
    }

    private void Done(Conversation conversation, MessageSide side)
    {
        events.Publish(conversation.Id);
        metrics.MessageSent(conversation.Kind, side);
    }

    // --- Names and links ---

    // Customers are shown by display name; their email only to admins.
    private static string CustomerLabel(bool exists, string? displayName, string? email)
    {
        if (!exists)
            return DeletedAccount;
        var name = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        return (name, email) switch
        {
            (not null, not null) => $"{name} ({email})",
            (not null, null) => name,
            (null, not null) => email,
            _ => "Customer",
        };
    }

    private static string SenderName(Conversation conversation, Message message, bool insider, bool admin)
    {
        var staffName = message.Sender is null ? null : message.Sender.DisplayName ?? message.Sender.Email;
        return message.Side switch
        {
            MessageSide.Customer => CustomerLabel(message.Sender is not null, message.Sender?.DisplayName,
                admin ? message.Sender?.Email : null),
            MessageSide.Theater => insider && staffName is not null
                ? $"{conversation.Theater?.Name} ({staffName})"
                : conversation.Theater?.Name ?? "The theater",
            _ => insider && staffName is not null ? $"{SupportName} ({staffName})" : SupportName,
        };
    }

    public static string CustomerLink(Conversation conversation) => $"messages/{conversation.Id}";

    public static string StaffLink(Conversation conversation) => $"manage/{conversation.TheaterId}/messages/{conversation.Id}";

    public static string AdminLink(Conversation conversation) => $"admin/messages/{conversation.Id}";

    private static string Preview(string? body)
    {
        if (body is null)
            return "";
        var line = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= PreviewLength ? line : line[..(PreviewLength - 1)] + "…";
    }

    // --- Validation ---

    private static string CleanSubject(string? subject)
    {
        var clean = subject?.Trim() ?? "";
        if (clean.Length == 0)
            throw new AppValidationException("Enter a subject.");
        if (clean.Length > Conversation.MaxSubjectLength)
            throw new AppValidationException($"Keep the subject to {Conversation.MaxSubjectLength} characters.");
        return clean;
    }

    private static string CleanBody(string? body)
    {
        var clean = body?.Trim() ?? "";
        if (clean.Length == 0)
            throw new AppValidationException("Write a message.");
        if (clean.Length > Message.MaxBodyLength)
            throw new AppValidationException($"Keep messages to {Message.MaxBodyLength} characters.");
        return clean;
    }
}
