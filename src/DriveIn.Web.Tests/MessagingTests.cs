using System.Diagnostics.Metrics;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using static DriveIn.Web.Authorization.TheaterPermissions;

namespace DriveIn.Web.Tests;

public class MessagingTests
{
    // A live theater with an owner, a customer, and employees with and without the messages permissions.
    internal sealed class World : IAsyncDisposable
    {
        public required TestApp App { get; init; }
        public required Theater Theater { get; init; }
        public required ApplicationUser Owner { get; init; }
        public required ApplicationUser Customer { get; init; }
        public required ApplicationUser Viewer { get; init; }   // messages.view only
        public required ApplicationUser Replier { get; init; }  // messages.view + messages.reply
        public required ApplicationUser Gate { get; init; }     // no messages permissions
        public required ApplicationUser Admin { get; init; }

        public MessagingService Messaging => App.Get<MessagingService>();
        public NotificationService Notifications => App.Get<NotificationService>();

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    internal static async Task<World> SetUpAsync()
    {
        var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        var theater = await app.CreateTheaterAsync("Starlight", owner.Id);
        var viewer = await app.CreateUserAsync("viewer@example.com", employeeTheaterId: theater.Id);
        await app.GrantAsync(viewer, ViewMessages);
        var replier = await app.CreateUserAsync("replier@example.com", employeeTheaterId: theater.Id);
        await app.GrantAsync(replier, ViewMessages, ReplyMessages);
        var gate = await app.CreateUserAsync("gate@example.com", employeeTheaterId: theater.Id);
        await app.GrantAsync(gate, AdmitGuests);
        return new World
        {
            App = app,
            Theater = theater,
            Owner = owner,
            Customer = await app.CreateUserAsync("customer@example.com"),
            Viewer = viewer,
            Replier = replier,
            Gate = gate,
            Admin = await app.CreateUserAsync("admin@example.com", admin: true),
        };
    }

    private static Task<int> AskAsync(World w, string body = "Do you allow dogs?") =>
        w.Messaging.StartWithTheaterAsync(Principals.For(w.Customer), w.Theater.Id, "Dogs", body);

    private static async Task<List<Notification>> NotificationsAsync(TestApp app)
    {
        await using var db = app.Db();
        return await db.Notifications.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task A_customer_messages_a_theater_and_staff_with_messages_view_are_notified()
    {
        await using var w = await SetUpAsync();

        var id = await AskAsync(w);

        var notified = (await NotificationsAsync(w.App)).ToDictionary(n => n.UserId);
        Assert.Equal(new[] { w.Owner.Id, w.Viewer.Id, w.Replier.Id }.Order(), notified.Keys.Order());
        Assert.All(notified.Values, n =>
        {
            Assert.Equal($"manage/{w.Theater.Id}/messages/{id}", n.Link);
            Assert.Equal("New message from Customer", n.Title);
            Assert.Equal(id, n.ConversationId);
        });

        var mine = Assert.Single(await w.Messaging.ListMineAsync(Principals.For(w.Customer)));
        Assert.Equal("Dogs", mine.Subject);
        Assert.Equal("Starlight", mine.TheaterName);
        Assert.False(mine.Unread); // you've read what you wrote
        Assert.Equal("Do you allow dogs?", mine.Preview);

        var inbox = Assert.Single(await w.Messaging.ListForTheaterAsync(Principals.For(w.Viewer), w.Theater.Id));
        Assert.True(inbox.Unread);
        Assert.Equal("Customer", inbox.CustomerName); // no display name, and staff don't see emails
    }

    [Fact]
    public async Task Staff_reply_reads_as_the_theater_to_the_customer_and_names_the_staff_member_inside()
    {
        await using var w = await SetUpAsync();
        var id = await AskAsync(w);

        await w.Messaging.PostAsync(Principals.For(w.Replier), id, "  Leashed dogs are welcome.  ");

        var forCustomer = await w.Messaging.GetAsync(Principals.For(w.Customer), id);
        Assert.True(forCustomer.IsCustomer);
        Assert.True(forCustomer.CanReply);
        Assert.False(forCustomer.CanClose);
        var reply = forCustomer.Messages[1];
        Assert.Equal(MessageSide.Theater, reply.Side);
        Assert.Equal("Starlight", reply.SenderName);
        Assert.Equal("Leashed dogs are welcome.", reply.Body);
        Assert.True(forCustomer.Messages[0].Mine);

        var forOwner = await w.Messaging.GetAsync(Principals.For(w.Owner), id);
        Assert.Equal("Starlight (replier@example.com)", forOwner.Messages[1].SenderName);
        Assert.True(forOwner.CanReply);
        Assert.True(forOwner.CanClose);

        var toCustomer = Assert.Single(await NotificationsAsync(w.App), n => n.UserId == w.Customer.Id);
        Assert.Equal($"messages/{id}", toCustomer.Link);
        Assert.Equal("New message from Starlight", toCustomer.Title);
    }

    [Fact]
    public async Task Who_can_read_and_reply_in_a_theater_conversation()
    {
        await using var w = await SetUpAsync();
        var id = await AskAsync(w);
        var other = Principals.For(await w.App.CreateUserAsync("other@example.com"));

        // Another customer, and staff without messages.view, can't see it.
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Messaging.GetAsync(other, id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Messaging.PostAsync(other, id, "hi"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Messaging.GetAsync(Principals.For(w.Gate), id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Messaging.ListForTheaterAsync(Principals.For(w.Gate), w.Theater.Id));

        // messages.view reads but can't reply or close.
        var viewer = Principals.For(w.Viewer);
        var view = await w.Messaging.GetAsync(viewer, id);
        Assert.False(view.CanReply);
        Assert.False(view.CanClose);
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Messaging.PostAsync(viewer, id, "hi"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Messaging.SetClosedAsync(viewer, id, true));

        // Admins oversee theater conversations, read-only, and see the customer's email.
        var admin = Principals.For(w.Admin, admin: true);
        var asAdmin = await w.Messaging.GetAsync(admin, id);
        Assert.False(asAdmin.CanReply);
        Assert.False(asAdmin.CanClose);
        Assert.Equal("customer@example.com", asAdmin.CustomerEmail);
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Messaging.PostAsync(admin, id, "hi"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Messaging.SetClosedAsync(admin, id, true));
        Assert.Single(await w.Messaging.ListForTheaterAsync(admin, w.Theater.Id));
        Assert.Single(await w.Messaging.ListTheaterConversationsAsync(admin, w.Theater.Id));
        Assert.Empty(await w.Messaging.ListTheaterConversationsAsync(admin, w.Theater.Id + 100));
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Messaging.ListTheaterConversationsAsync(Principals.For(w.Owner)));

        await Assert.ThrowsAsync<NotFoundException>(() => w.Messaging.GetAsync(Principals.For(w.Customer), id + 100));
    }

    [Fact]
    public async Task Closed_conversations_take_no_messages_until_reopened()
    {
        await using var w = await SetUpAsync();
        var id = await AskAsync(w);
        var replier = Principals.For(w.Replier);
        var customer = Principals.For(w.Customer);

        await w.Messaging.SetClosedAsync(replier, id, true);
        Assert.Empty(await w.Messaging.ListForTheaterAsync(replier, w.Theater.Id));
        Assert.Single(await w.Messaging.ListForTheaterAsync(replier, w.Theater.Id, closed: true));
        var closed = await w.Messaging.GetAsync(customer, id);
        Assert.True(closed.IsClosed);
        Assert.False(closed.CanReply);
        await Assert.ThrowsAsync<AppValidationException>(() => w.Messaging.PostAsync(customer, id, "Hello?"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Messaging.SetClosedAsync(customer, id, false));

        await w.Messaging.SetClosedAsync(replier, id, false);
        await w.Messaging.PostAsync(customer, id, "Hello?");
        Assert.Equal(2, (await w.Messaging.GetAsync(customer, id)).Messages.Count);
    }

    [Fact]
    public async Task Private_theaters_and_your_own_theater_cant_be_messaged()
    {
        await using var w = await SetUpAsync();
        await using (var db = w.App.Db())
        {
            db.Theaters.Add(new Theater { Name = "Demo", Slug = "demo", Mode = TheaterMode.Demo, OwnerId = w.Owner.Id });
            db.Theaters.Add(new Theater { Name = "Shut", Slug = "shut", IsActive = false });
            await db.SaveChangesAsync();
        }
        await using var read = w.App.Db();
        var demo = await read.Theaters.SingleAsync(t => t.Slug == "demo");
        var shut = await read.Theaters.SingleAsync(t => t.Slug == "shut");
        var customer = Principals.For(w.Customer);

        await Assert.ThrowsAsync<NotFoundException>(() => w.Messaging.StartWithTheaterAsync(customer, demo.Id, "Hi", "Hi"));
        await Assert.ThrowsAsync<NotFoundException>(() => w.Messaging.StartWithTheaterAsync(customer, shut.Id, "Hi", "Hi"));
        await Assert.ThrowsAsync<NotFoundException>(() => w.Messaging.StartWithTheaterAsync(customer, 9999, "Hi", "Hi"));
        await Assert.ThrowsAsync<AppValidationException>(() =>
            w.Messaging.StartWithTheaterAsync(Principals.For(w.Gate), w.Theater.Id, "Hi", "Hi"));
        await Assert.ThrowsAsync<AppValidationException>(() =>
            w.Messaging.StartWithTheaterAsync(Principals.For(w.Owner), w.Theater.Id, "Hi", "Hi"));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            w.Messaging.StartWithTheaterAsync(Principals.Anonymous, w.Theater.Id, "Hi", "Hi"));
    }

    [Fact]
    public async Task Subjects_and_messages_are_validated_and_new_conversations_are_limited_per_day()
    {
        await using var w = await SetUpAsync();
        var customer = Principals.For(w.Customer);
        var m = w.Messaging;

        await Assert.ThrowsAsync<AppValidationException>(() => m.StartWithTheaterAsync(customer, w.Theater.Id, " ", "Hi"));
        await Assert.ThrowsAsync<AppValidationException>(() => m.StartWithTheaterAsync(customer, w.Theater.Id, "Hi", " "));
        await Assert.ThrowsAsync<AppValidationException>(() =>
            m.StartWithTheaterAsync(customer, w.Theater.Id, new string('s', Conversation.MaxSubjectLength + 1), "Hi"));
        await Assert.ThrowsAsync<AppValidationException>(() =>
            m.StartWithTheaterAsync(customer, w.Theater.Id, "Hi", new string('b', Message.MaxBodyLength + 1)));
        var id = await m.StartWithTheaterAsync(customer, w.Theater.Id, "Hi", new string('b', Message.MaxBodyLength));
        await Assert.ThrowsAsync<AppValidationException>(() => m.PostAsync(customer, id, ""));

        for (var i = 1; i < MessagingService.MaxNewConversationsPerDay; i++)
            await m.StartWithTheaterAsync(customer, w.Theater.Id, $"Question {i}", "Hi");
        var limit = await Assert.ThrowsAsync<AppValidationException>(() => m.StartSupportAsync(customer, null, "One more", "Hi"));
        Assert.Contains("up to 10", limit.Message);
        w.App.Time.Advance(new RateLimitOptions().Messages.Window); // past the burst limit, which those 10 used up
        await m.PostAsync(customer, id, "Replies are still fine.");

        w.App.Time.Advance(TimeSpan.FromDays(1));
        await m.StartSupportAsync(customer, null, "Next day", "Hi");
    }

    [Fact]
    public async Task Support_conversations_go_to_admins_who_reply_and_close_them()
    {
        await using var w = await SetUpAsync();
        var secondAdmin = await w.App.CreateUserAsync("admin2@example.com", admin: true);
        var owner = Principals.For(w.Owner);
        var admin = Principals.For(w.Admin, admin: true);

        var id = await w.Messaging.StartSupportAsync(owner, w.Theater.Id, "Going live", "When can we go live?");

        var notified = await NotificationsAsync(w.App);
        Assert.Equal(new[] { w.Admin.Id, secondAdmin.Id }.Order(), notified.Select(n => n.UserId).Order());
        Assert.All(notified, n => Assert.Equal($"admin/messages/{id}", n.Link));

        var inbox = Assert.Single(await w.Messaging.ListSupportAsync(admin));
        Assert.Equal("Starlight", inbox.TheaterName);
        Assert.Equal("owner@example.com", inbox.CustomerName);
        Assert.True(inbox.Unread);

        var thread = await w.Messaging.GetAsync(admin, id);
        Assert.Equal("Starlight", thread.AboutTheaterName);
        Assert.True(thread.CanReply);
        Assert.True(thread.CanClose);
        Assert.False(Assert.Single(await w.Messaging.ListSupportAsync(admin)).Unread);

        await w.Messaging.PostAsync(admin, id, "Ask from your manage page when you're ready.");
        var forOwner = await w.Messaging.GetAsync(owner, id);
        Assert.Equal(MessagingService.SupportName, forOwner.Messages[1].SenderName);
        Assert.Equal($"{MessagingService.SupportName} (admin@example.com)",
            (await w.Messaging.GetAsync(Principals.For(secondAdmin, admin: true), id)).Messages[1].SenderName);
        var toOwner = Assert.Single(await NotificationsAsync(w.App), n => n.UserId == w.Owner.Id);
        Assert.Equal($"New message from {MessagingService.SupportName}", toOwner.Title);
        Assert.Equal($"messages/{id}", toOwner.Link);

        await w.Messaging.SetClosedAsync(admin, id, true);
        Assert.Empty(await w.Messaging.ListSupportAsync(admin));
        Assert.Single(await w.Messaging.ListSupportAsync(admin, closed: true));

        // Only admins see support conversations other than their own, and the about-theater must be yours.
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Messaging.GetAsync(Principals.For(w.Replier), id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.Messaging.ListSupportAsync(owner));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            w.Messaging.StartSupportAsync(Principals.For(w.Customer), w.Theater.Id, "Hi", "Hi"));
        await Assert.ThrowsAsync<NotFoundException>(() => w.Messaging.StartSupportAsync(owner, 9999, "Hi", "Hi"));
        // Someone with no theater yet can still ask.
        await w.Messaging.StartSupportAsync(Principals.For(w.Customer), null, "Thinking of signing up", "How much is it?");
    }

    [Fact]
    public async Task Each_reader_has_their_own_unread_state()
    {
        await using var w = await SetUpAsync();
        var id = await AskAsync(w);
        var owner = Principals.For(w.Owner);
        var viewer = Principals.For(w.Viewer);

        await w.Messaging.GetAsync(owner, id);

        Assert.False(Assert.Single(await w.Messaging.ListForTheaterAsync(owner, w.Theater.Id)).Unread);
        Assert.True(Assert.Single(await w.Messaging.ListForTheaterAsync(viewer, w.Theater.Id)).Unread);
        Assert.Equal(0, await w.Notifications.CountUnreadAsync(owner));
        Assert.Equal(1, await w.Notifications.CountUnreadAsync(viewer));

        // A new message makes it unread again.
        w.App.Time.Advance(TimeSpan.FromMinutes(1));
        await w.Messaging.PostAsync(Principals.For(w.Customer), id, "Also, cats?");
        Assert.True(Assert.Single(await w.Messaging.ListForTheaterAsync(owner, w.Theater.Id)).Unread);
    }

    [Fact]
    public async Task More_messages_before_reading_update_one_notification()
    {
        await using var w = await SetUpAsync();
        var id = await AskAsync(w);
        var customer = Principals.For(w.Customer);

        w.App.Time.Advance(TimeSpan.FromMinutes(1));
        await w.Messaging.PostAsync(customer, id, "And cats?");
        await w.Messaging.PostAsync(customer, id, "And parrots?");

        var owners = (await NotificationsAsync(w.App)).Where(n => n.UserId == w.Owner.Id).ToList();
        var one = Assert.Single(owners);
        Assert.Equal(3, one.Count);
        Assert.Equal("3 new messages from Customer", one.Title);
        Assert.Equal(w.App.Time.GetUtcNow(), one.UpdatedAt);

        // Once read, the next message starts a new notification.
        await w.Messaging.GetAsync(Principals.For(w.Owner), id);
        await w.Messaging.PostAsync(customer, id, "Hello?");
        owners = (await NotificationsAsync(w.App)).Where(n => n.UserId == w.Owner.Id).ToList();
        Assert.Equal(2, owners.Count);
        Assert.Single(owners, n => n.ReadAt is null && n.Count == 1);
    }

    [Fact]
    public async Task Posting_publishes_the_conversation_and_the_recipients_notifications()
    {
        await using var w = await SetUpAsync();
        var conversations = new List<int>();
        var users = new List<string>();
        w.App.MessageEvents.Changed += conversations.Add;
        w.App.NotificationEvents.Changed += users.Add;

        var id = await AskAsync(w);

        Assert.Equal([id], conversations);
        Assert.Equal(new[] { w.Owner.Id, w.Viewer.Id, w.Replier.Id }.Order(), users.Order());
    }

    [Fact]
    public async Task Messages_are_counted_by_kind_and_side()
    {
        await using var w = await SetUpAsync();
        using var sent = new MetricCollector<long>(w.App.Get<IMeterFactory>(), DriveInMetrics.MeterName, "drivein.messages.sent");

        var id = await AskAsync(w);
        await w.Messaging.PostAsync(Principals.For(w.Owner), id, "Yes");
        await w.Messaging.StartSupportAsync(Principals.For(w.Customer), null, "Hi", "Hi");

        var tags = sent.GetMeasurementSnapshot().Select(m => $"{m.Tags["kind"]}/{m.Tags["side"]}").ToList();
        Assert.Equal(["theater/customer", "theater/theater", "support/customer"], tags);
    }

    [Fact]
    public async Task Deleting_an_account_keeps_the_conversation_as_a_deleted_account()
    {
        await using var w = await SetUpAsync();
        var id = await AskAsync(w);

        // EF InMemory doesn't apply SET NULL, so do what Postgres does on delete.
        await using (var db = w.App.Db())
        {
            foreach (var c in db.Conversations.Where(c => c.CustomerId == w.Customer.Id))
                c.CustomerId = null;
            foreach (var m in db.Messages.Where(m => m.SenderId == w.Customer.Id))
                m.SenderId = null;
            await db.SaveChangesAsync();
        }
        await using (var scope = w.App.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            await users.DeleteAsync((await users.FindByIdAsync(w.Customer.Id))!);
        }

        var thread = await w.Messaging.GetAsync(Principals.For(w.Owner), id);
        Assert.Equal(MessagingService.DeletedAccount, thread.CustomerName);
        Assert.Equal(MessagingService.DeletedAccount, thread.Messages[0].SenderName);
        Assert.Equal(MessagingService.DeletedAccount, Assert.Single(await w.Messaging.ListForTheaterAsync(Principals.For(w.Owner), w.Theater.Id)).CustomerName);
        // Nobody's left to reply to, but the theater can still write (it just isn't delivered to anyone).
        await w.Messaging.PostAsync(Principals.For(w.Owner), id, "Thanks for asking.");
    }

    [Fact]
    public async Task Display_names_show_in_place_of_Customer()
    {
        await using var w = await SetUpAsync();
        await using (var db = w.App.Db())
        {
            (await db.Users.SingleAsync(u => u.Id == w.Customer.Id)).DisplayName = "Pat";
            await db.SaveChangesAsync();
        }

        var id = await AskAsync(w);

        Assert.Equal("Pat", (await w.Messaging.GetAsync(Principals.For(w.Owner), id)).CustomerName);
        Assert.Equal("Pat (customer@example.com)", (await w.Messaging.GetAsync(Principals.For(w.Admin, admin: true), id)).CustomerName);
        Assert.All((await NotificationsAsync(w.App)), n => Assert.Equal("New message from Pat", n.Title));
    }

    [Fact]
    public async Task Bursts_of_messages_are_limited_per_person()
    {
        await using var w = await SetUpAsync();
        var id = await AskAsync(w); // the first message counts too
        var limit = new RateLimitOptions().Messages;
        var customer = Principals.For(w.Customer);

        for (var i = 1; i < limit.PermitLimit; i++)
            await w.Messaging.PostAsync(customer, id, $"Follow-up {i}");
        var refused = await Assert.ThrowsAsync<AppValidationException>(() => w.Messaging.PostAsync(customer, id, "One more"));

        Assert.StartsWith("Too many attempts", refused.Message);
        await w.Messaging.PostAsync(Principals.For(w.Replier), id, "Staff can still answer.");
        w.App.Time.Advance(limit.Window);
        await w.Messaging.PostAsync(customer, id, "One more");
    }
}
