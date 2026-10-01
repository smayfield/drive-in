using DriveIn.Web.Components.Layout;
using DriveIn.Web.Components.Pages;
using DriveIn.Web.Components.Pages.Legal;
using DriveIn.Web.Components.Pages.Marketing;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;

namespace DriveIn.Web.Tests.Pages;

// The home, marketing, legal and error pages, the layouts, and signing up a theater.
public class SitePageTests
{
    private static RenderFragment Body(string text) => b => b.AddContent(0, text);

    [Fact]
    public async Task The_home_page_invites_visitors_to_sign_in_and_members_into_the_app()
    {
        await using var app = new TestApp();
        await using var host = new PageHost(app);

        var anonymous = host.Render<Home>();
        Assert.Contains("Start your free demo", anonymous.Text());
        Assert.Contains("Account/Login", anonymous.Markup);

        host.SignIn(await TicketSalesTests.BuyerAsync(app));
        var member = host.Context.Render<Home>();
        Assert.Contains("Open the app", member.Text());
    }

    [Theory]
    [InlineData(typeof(Features), "Features")]
    [InlineData(typeof(Faq), "FAQ")]
    [InlineData(typeof(LegalIndex), "Terms")]
    [InlineData(typeof(Privacy), "Privacy")]
    [InlineData(typeof(Terms), "Terms of Service")]
    [InlineData(typeof(License), "License")]
    [InlineData(typeof(NotFound), "Reel not found")]
    [InlineData(typeof(Error), "An error occurred while processing your request.")]
    public async Task Static_pages_render(Type page, string expected)
    {
        await using var host = new PageHost();

        var rendered = host.Context.Render<DynamicComponent>(p => p.Add(x => x.Type, page));

        Assert.Contains(expected, rendered.Text());
    }

    [Fact]
    public async Task Pricing_shows_the_plan_price_and_season_examples()
    {
        await using var host = new PageHost();

        var page = host.Render<Pricing>();

        var text = page.Text();
        Assert.Contains("$49 per screen, per month in season", text);
        Assert.Contains("1 screen May 1 – Sep 30 5 $245.00", text);
    }

    [Fact]
    public async Task The_app_layout_shows_links_for_who_is_signed_in()
    {
        await using var app = new TestApp();
        await using (var visitor = new PageHost(app))
        {
            var anonymous = visitor.Context.Render<AppLayout>(p => p.Add(x => x.Body, Body("Hello")));
            Assert.Contains("Hello", anonymous.Text());
            Assert.Contains("Register", anonymous.Text());
            Assert.DoesNotContain("My tickets", anonymous.Text());
        }

        var app2 = new TestApp();
        await using var host = new PageHost(app2).SignIn(await app2.CreateUserAsync("admin@example.com", admin: true), admin: true);
        var admin = host.Context.Render<AppLayout>(p => p.Add(x => x.Body, Body("Hello")));
        var text = admin.Text();
        Assert.Contains("My tickets", text);
        Assert.Contains("Manage", text);
        Assert.Contains("Admin: Users", text);
        Assert.Contains("Sign out", text);

        admin.Find("button[aria-label='Open menu']").Click();
    }

    [Fact]
    public async Task The_static_layout_offers_owners_manage_and_others_sign_up()
    {
        var s = await TicketSalesTests.SetUpAsync();
        await using var host = new PageHost(s.App).SignIn(s.Owner);

        var owner = host.Context.Render<AccountLayout>(p => p.Add(x => x.Body, Body("Hello")));
        Assert.Contains("Manage", owner.Text());
        Assert.DoesNotContain("Sign up a theater", owner.Text());

        host.SignIn(await TicketSalesTests.BuyerAsync(s.App));
        var buyer = host.Context.Render<AccountLayout>(p => p.Add(x => x.Body, Body("Hello")));
        Assert.Contains("Sign up a theater", buyer.Text());

        var marketing = host.Context.Render<MarketingLayout>(p => p.Add(x => x.Body, Body("Hello")));
        Assert.Contains("Hello", marketing.Text());
    }

    // --- Get started ---

    [Fact]
    public async Task Visitors_are_asked_to_create_an_account_first()
    {
        await using var host = new PageHost();

        var page = host.Render<GetStarted>();

        page.WaitForText("First, your account");
        Assert.Contains("Account/Register?ReturnUrl=%2Fget-started", page.Markup);
    }

    [Fact]
    public async Task Employees_cant_sign_up_a_theater()
    {
        var s = await TicketSalesTests.SetUpAsync();
        var staff = await s.App.CreateUserAsync("staff@example.com", s.Theater.Id);
        await using var host = new PageHost(s.App).SignIn(staff);

        var page = host.Render<GetStarted>();

        page.WaitForText("You're signed in with an employee account");
    }

    [Fact]
    public async Task Signing_up_creates_a_demo_theater_and_opens_it()
    {
        await using var app = new TestApp();
        var owner = await app.CreateUserAsync("owner@example.com");
        await using var host = new PageHost(app).SignIn(owner);
        var page = host.Render<GetStarted>();
        page.WaitForText("Create my demo theater");

        page.SetField("Theater name", "Starlight Drive-In");
        Assert.Equal("starlight-drive-in", page.Field("Web address").GetAttribute("value"));
        page.SetField("City", "Austin");
        page.SetField("State", "TX");
        host.Select(page, "Time zone", "Central (Chicago)");
        host.Select(page, "Screens", "2");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.Contains("mud-alert-text-error", page.Markup)); // the terms weren't accepted

        page.Check("I agree to the");
        page.Find("form").Submit();

        await using var db = app.Db();
        page.WaitForAssertion(() => Assert.Matches(@"manage/\d+$", host.Nav.Uri));
        var theater = await db.Theaters.Include(t => t.Screens).SingleAsync();
        Assert.Equal(("starlight-drive-in", TheaterMode.Demo, 2), (theater.Slug, theater.Mode, theater.Screens.Count));
    }

    [Fact]
    public async Task A_chosen_web_address_is_kept()
    {
        await using var app = new TestApp();
        await using var host = new PageHost(app).SignIn(await app.CreateUserAsync("owner@example.com"));
        var page = host.Render<GetStarted>();
        page.WaitForText("Create my demo theater");

        page.SetField("Web address", "the-starlight");
        page.SetField("Theater name", "Starlight Drive-In");
        host.Select(page, "Time zone", "Central (Chicago)");
        page.Check("I agree to the");
        page.Find("form").Submit();

        page.WaitForAssertion(() => Assert.Matches(@"manage/\d+$", host.Nav.Uri));
        await using var db = app.Db();
        Assert.Equal("the-starlight", (await db.Theaters.SingleAsync()).Slug);
    }
}
