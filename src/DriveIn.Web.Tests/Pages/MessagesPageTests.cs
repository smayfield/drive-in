using Bunit;
using DriveIn.Web.Components.Layout;
using DriveIn.Web.Components.Pages;
using DriveIn.Web.Components.Pages.Admin;
using DriveIn.Web.Components.Pages.Manage;
using DriveIn.Web.Components.Pages.Messages;
using DriveIn.Web.Components.Pages.Theaters;
using DriveIn.Web.Components.Shared;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using static DriveIn.Web.Tests.MessagingTests;
using AccountNotifications = DriveIn.Web.Components.Account.Pages.Manage.Notifications;

namespace DriveIn.Web.Tests.Pages;

// Messages (customer, theater and admin pages), the notification bell and page, and the email setting.
public class MessagesPageTests
{
    private static Task<int> AskAsync(World w) =>
        w.Messaging.StartWithTheaterAsync(Principals.For(w.Customer), w.Theater.Id, "Dogs", "Do you allow dogs?");

    private static async Task<List<Message>> MessagesAsync(TestApp app)
    {
        await using var db = app.Db();
        return await db.Messages.AsNoTracking().OrderBy(m => m.Id).ToListAsync();
    }

    // --- Customers ---

    [Fact]
    public async Task The_theater_page_offers_customers_a_message_button_but_not_staff()
    {
        await using var w = await SetUpAsync();
        await using var host = new PageHost(w.App).SignIn(w.Customer);

        var page = host.Render<Details>(p => p.Add(x => x.Slug, w.Theater.Slug));
        page.WaitForText("Message the theater");
        Assert.Contains("messages/new?theater=starlight", page.Markup);

        host.SignIn(w.Replier);
        var staff = host.Render<Details>(p => p.Add(x => x.Slug, w.Theater.Slug));
        staff.WaitForText("Find us");
        Assert.DoesNotContain("Message the theater", staff.Text());
    }

    [Fact]
    public async Task A_customer_writes_to_a_theater_and_lands_on_the_conversation()
    {
        await using var w = await SetUpAsync();
        await using var host = new PageHost(w.App).SignIn(w.Customer);
        host.Nav.NavigateTo("messages/new?theater=starlight");

        var page = host.Render<NewMessage>();
        page.WaitForText("Message Starlight");
        Assert.Empty(page.FindAll(".mud-select")); // no "about" picker when writing to a theater

        page.ClickButton("Send");
        page.WaitForText("Enter a subject.");

        page.SetField("Subject", "Dogs");
        page.SetField("Message", "Do you allow dogs?");
        page.ClickButton("Send");

        var conversation = await w.App.Db().Conversations.SingleAsync();
        page.WaitForAssertion(() => Assert.EndsWith($"messages/{conversation.Id}", host.Nav.Uri));
        Assert.Equal(w.Theater.Id, conversation.TheaterId);
        Assert.Equal("Do you allow dogs?", Assert.Single(await MessagesAsync(w.App)).Body);
    }

    [Fact]
    public async Task Staff_are_pointed_to_their_inbox_and_unknown_theaters_are_not_found()
    {
        await using var w = await SetUpAsync();
        await using var host = new PageHost(w.App).SignIn(w.Owner);
        host.Nav.NavigateTo("messages/new?theater=starlight");

        var page = host.Render<NewMessage>();
        page.WaitForText("You work for Starlight");
        Assert.Contains($"manage/{w.Theater.Id}/messages", page.Markup);

        host.Nav.NavigateTo("messages/new?theater=nowhere");
        var missing = host.Render<NewMessage>();
        missing.WaitForText("Theater not found.");
    }

    [Fact]
    public async Task An_owner_contacts_support_about_one_of_their_theaters()
    {
        await using var w = await SetUpAsync();
        await using var host = new PageHost(w.App).SignIn(w.Owner);
        host.Nav.NavigateTo("messages/new");

        var page = host.Render<NewMessage>();
        page.WaitForText("Contact Drive-In Online");
        host.Select(page, "About (optional)", "Starlight");
        page.SetField("Subject", "Going live");
        page.SetField("Message", "When can we go live?");
        page.ClickButton("Send");

        page.WaitForAssertion(() => Assert.Matches(@"messages/\d+$", host.Nav.Uri));
        var conversation = await w.App.Db().Conversations.SingleAsync();
        Assert.Equal(ConversationKind.Support, conversation.Kind);
        Assert.Equal(w.Theater.Id, conversation.AboutTheaterId);
    }

    [Fact]
    public async Task The_inbox_lists_your_conversations()
    {
        await using var w = await SetUpAsync();
        await using var host = new PageHost(w.App).SignIn(w.Customer);

        var empty = host.Render<Inbox>();
        empty.WaitForText("No messages yet.");

        var id = await AskAsync(w);
        w.App.Time.Advance(TimeSpan.FromMinutes(1));
        await w.Messaging.PostAsync(Principals.For(w.Owner), id, "Leashed dogs are welcome.");
        var page = host.Render<Inbox>();
        page.WaitForText("Dogs");
        Assert.Contains("Unread:", page.Text());
        Assert.Contains("Starlight", page.Text());
        Assert.Contains("Leashed dogs are welcome.", page.Text());
        Assert.Contains($"messages/{id}", page.Markup);
    }

    [Fact]
    public async Task A_conversation_shows_the_thread_takes_replies_and_updates_live()
    {
        await using var w = await SetUpAsync();
        var id = await AskAsync(w);
        await using var host = new PageHost(w.App).SignIn(w.Customer);

        var page = host.Render<MessageConversation>(p => p.Add(x => x.Id, id));
        page.WaitForText("Do you allow dogs?");
        Assert.Contains("With Starlight", page.Text());
        Assert.Contains("You ·", page.Text());
        Assert.DoesNotContain("Close conversation", page.Text());

        page.SetField("Reply", "Also, cats?");
        page.ClickButton("Send");
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".message").Count));
        Assert.Empty(page.Field("Reply").GetAttribute("value") ?? "");

        // The theater answers from elsewhere: it appears without a reload.
        await w.Messaging.PostAsync(Principals.For(w.Replier), id, "Both are welcome.");
        page.WaitForText("Both are welcome.");
        Assert.Contains("Starlight ·", page.Text());

        // Closed by the theater: the reply box goes.
        await w.Messaging.SetClosedAsync(Principals.For(w.Replier), id, true);
        page.WaitForText("This conversation is closed.");
        Assert.Empty(page.Fields("Reply"));
    }

    [Fact]
    public async Task Someone_elses_conversation_is_denied()
    {
        await using var w = await SetUpAsync();
        var id = await AskAsync(w);
        await using var host = new PageHost(w.App).SignIn(await w.App.CreateUserAsync("other@example.com"));

        host.Render<MessageConversation>(p => p.Add(x => x.Id, id));

        Assert.EndsWith("Account/AccessDenied", host.Nav.Uri);
    }

    // --- Theaters ---

    [Fact]
    public async Task Staff_read_reply_and_close_from_the_theater_inbox()
    {
        await using var w = await SetUpAsync();
        var id = await AskAsync(w);
        await using var host = new PageHost(w.App).SignIn(w.Replier);

        var list = host.Render<ManageMessages>(p => p.Add(x => x.Id, w.Theater.Id));
        list.WaitForText("Choose a conversation to read it.");
        Assert.Contains("Unread:", list.Text());
        Assert.Contains($"manage/{w.Theater.Id}/messages/{id}", list.Markup);
        Assert.Contains("manage-tab active", list.Find($"a[href='manage/{w.Theater.Id}/messages']").OuterHtml);

        var page = host.Render<ManageMessages>(p => p.Add(x => x.Id, w.Theater.Id).Add(x => x.ConversationId, id));
        page.WaitForText("Do you allow dogs?");
        Assert.Contains("From Customer to Starlight", page.Text());
        page.WaitForAssertion(() => Assert.DoesNotContain("Unread:", page.Text()));

        page.SetField("Reply", "Leashed dogs are welcome.");
        page.ClickButton("Send");
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".message-mine")));
        Assert.Contains("Leashed dogs are welcome.", page.Find(".message-mine").TextContent);

        page.ClickButton("Close conversation");
        page.WaitForText("This conversation is closed.");
        page.WaitForText("No open conversations.");
        page.ClickButton("Reopen");
        page.WaitForAssertion(() => Assert.Contains("Close conversation", page.Text()));
    }

    [Fact]
    public async Task New_customer_messages_appear_in_the_open_inbox()
    {
        await using var w = await SetUpAsync();
        await using var host = new PageHost(w.App).SignIn(w.Owner);
        var page = host.Render<ManageMessages>(p => p.Add(x => x.Id, w.Theater.Id));
        page.WaitForText("No open conversations.");

        await AskAsync(w);

        page.WaitForText("Dogs");
    }

    [Fact]
    public async Task Viewers_read_without_replying_and_staff_without_the_permission_are_turned_away()
    {
        await using var w = await SetUpAsync();
        var id = await AskAsync(w);
        await using var host = new PageHost(w.App).SignIn(w.Viewer);

        var page = host.Render<ManageMessages>(p => p.Add(x => x.Id, w.Theater.Id).Add(x => x.ConversationId, id));
        page.WaitForText("You can read this conversation but not reply.");
        Assert.Empty(page.Fields("Reply"));

        host.SignIn(w.Gate);
        host.Render<ManageMessages>(p => p.Add(x => x.Id, w.Theater.Id));
        Assert.EndsWith("Account/AccessDenied", host.Nav.Uri);
    }

    [Fact]
    public async Task The_messages_tab_shows_only_with_messages_view()
    {
        await using var w = await SetUpAsync();
        await using var host = new PageHost(w.App).SignIn(w.Viewer);
        var withTab = host.Render<ManageHeader>(p => p.Add(x => x.Theater, w.Theater).Add(x => x.Title, "Probe"));
        withTab.WaitForText("Messages");

        host.SignIn(w.Gate);
        var without = host.Render<ManageHeader>(p => p.Add(x => x.Theater, w.Theater).Add(x => x.Title, "Probe"));
        without.WaitForText("Gate");
        Assert.DoesNotContain("Messages", without.Text());
    }

    // --- Admins ---

    [Fact]
    public async Task Admins_answer_support_and_read_theater_conversations()
    {
        await using var w = await SetUpAsync();
        var support = await w.Messaging.StartSupportAsync(Principals.For(w.Owner), w.Theater.Id, "Going live", "When can we go live?");
        var theaterConversation = await AskAsync(w);
        await using var host = new PageHost(w.App).SignIn(w.Admin, admin: true);

        var inbox = host.Render<AdminMessages>();
        inbox.WaitForText("Going live");
        Assert.Contains("owner@example.com · about Starlight", inbox.Text());

        var page = host.Render<AdminMessages>(p => p.Add(x => x.ConversationId, support));
        page.WaitForText("When can we go live?");
        page.SetField("Reply", "Ask from your manage page.");
        page.ClickButton("Send");
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".message").Count));
        Assert.Contains("Ask from your manage page.", page.Find(".message-mine").TextContent);
        page.ClickButton("Close conversation");
        page.WaitForText("No open support conversations.");

        host.Nav.NavigateTo($"admin/messages/{theaterConversation}?view=theaters");
        var oversight = host.Render<AdminMessages>(p => p.Add(x => x.ConversationId, theaterConversation));
        oversight.WaitForText("Do you allow dogs?");
        Assert.Contains("You can read this conversation but not reply.", oversight.Text());
        Assert.Contains("customer@example.com → Starlight", oversight.Text());

        host.Select(oversight, "Theater", "Starlight");
        oversight.WaitForAssertion(() => Assert.EndsWith($"admin/messages?view=theaters&theater={w.Theater.Id}", host.Nav.Uri));
    }

    // --- Notifications ---

    [Fact]
    public async Task The_bell_counts_unread_live_and_opens_them()
    {
        await using var w = await SetUpAsync();
        await using var host = new PageHost(w.App).SignIn(w.Owner);
        var bell = host.Render<NotificationBell>();
        Assert.Empty(bell.FindAll(".mud-badge"));
        Assert.NotEmpty(bell.FindAll("button[aria-label='Notifications']"));

        var id = await AskAsync(w);
        bell.WaitForAssertion(() => Assert.NotEmpty(bell.FindAll("button[aria-label='Notifications, 1 unread']")));

        bell.Find("button[aria-label='Notifications, 1 unread']").Click();
        var popovers = host.Popovers!;
        popovers.WaitForAssertion(() => Assert.Contains("New message from Customer", popovers.Markup));
        popovers.FindAll(".mud-menu-item").First(i => i.TextContent.Contains("New message from Customer")).Click();

        bell.WaitForAssertion(() => Assert.EndsWith($"manage/{w.Theater.Id}/messages/{id}", host.Nav.Uri));
        bell.WaitForAssertion(() => Assert.NotEmpty(bell.FindAll("button[aria-label='Notifications']")));
    }

    [Fact]
    public async Task The_bell_marks_everything_read()
    {
        await using var w = await SetUpAsync();
        await AskAsync(w);
        await using var host = new PageHost(w.App).SignIn(w.Owner);
        var bell = host.Render<NotificationBell>();
        bell.WaitForAssertion(() => Assert.NotEmpty(bell.FindAll("button[aria-label='Notifications, 1 unread']")));

        bell.Find("button[aria-label='Notifications, 1 unread']").Click();
        var popovers = host.Popovers!;
        popovers.WaitForAssertion(() => Assert.Contains("Mark all read", popovers.Markup));
        popovers.FindAll("button").First(b => b.TextContent.Trim() == "Mark all read").Click();

        bell.WaitForAssertion(() => Assert.NotEmpty(bell.FindAll("button[aria-label='Notifications']")));
        Assert.Equal(0, await w.Notifications.CountUnreadAsync(Principals.For(w.Owner)));
    }

    [Fact]
    public async Task The_notifications_page_lists_opens_and_clears_them()
    {
        await using var w = await SetUpAsync();
        await using var host = new PageHost(w.App).SignIn(w.Owner);
        var page = host.Render<NotificationsPage>();
        page.WaitForText("No notifications.");

        var id = await AskAsync(w);
        page.WaitForText("New message from Customer");
        Assert.Contains("Unread:", page.Text());

        await w.Messaging.StartWithTheaterAsync(Principals.For(w.Customer), w.Theater.Id, "Snacks", "Popcorn?");
        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll(".conversation-item.unread").Count));
        page.ClickButton("Mark all read");
        page.WaitForAssertion(() => Assert.Empty(page.FindAll(".conversation-item.unread")));

        await w.Messaging.PostAsync(Principals.For(w.Customer), id, "Hello?");
        page.WaitForAssertion(() => Assert.Single(page.FindAll(".conversation-item.unread")));
        page.Find(".conversation-item.unread").Click();
        page.WaitForAssertion(() => Assert.EndsWith($"manage/{w.Theater.Id}/messages/{id}", host.Nav.Uri));
        Assert.Equal(0, await w.Notifications.CountUnreadAsync(Principals.For(w.Owner)));
    }

    [Fact]
    public async Task The_layouts_link_to_messages_and_count_notifications()
    {
        await using var w = await SetUpAsync();
        await AskAsync(w);
        await using var host = new PageHost(w.App).SignIn(w.Owner);

        var app = host.Context.Render<AppLayout>(p => p.Add(x => x.Body, Body("Hello")));
        Assert.Contains("href=\"messages\"", app.Markup);
        app.WaitForAssertion(() => Assert.NotEmpty(app.FindAll("button[aria-label='Notifications, 1 unread']")));

        var account = host.Context.Render<AccountLayout>(p => p.Add(x => x.Body, Body("Hello")));
        Assert.Contains("Notifications (1)", account.Text());
        Assert.Contains("href=\"messages\"", account.Markup);
    }

    [Fact]
    public async Task Email_notifications_can_be_turned_off_and_on()
    {
        await using var w = await SetUpAsync();
        await using var host = new PageHost(w.App).SignIn(w.Customer).UseRequest("POST");
        host.Nav.NavigateTo("Account/Manage/Notifications");

        var page = host.Render<AccountNotifications>();
        Assert.True(page.Find("input[type=checkbox]").HasAttribute("checked"));

        page.Find("input[type=checkbox]").Change(false);
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.EndsWith("Account/Manage/Notifications", host.Nav.Uri));
        await using (var db = w.App.Db())
            Assert.False((await db.Users.SingleAsync(u => u.Id == w.Customer.Id)).EmailNotifications);

        var again = host.Render<AccountNotifications>();
        again.Find("input[type=checkbox]").Change(true);
        again.Find("form").Submit();
        await using (var db = w.App.Db())
            again.WaitForAssertion(() => Assert.True(db.Users.AsNoTracking().Single(u => u.Id == w.Customer.Id).EmailNotifications));
    }

    [Fact]
    public async Task A_missing_user_on_the_settings_page_goes_to_invalid_user()
    {
        await using var host = new PageHost().SignIn(Principals.Create("gone")).UseRequest();

        host.Render<AccountNotifications>();

        Assert.EndsWith("Account/InvalidUser", host.Nav.Uri);
    }

    private static RenderFragment Body(string text) => b => b.AddContent(0, text);
}
