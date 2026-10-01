using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using DriveIn.Web.Components.Account;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MudBlazor;
using MudBlazor.Services;

namespace DriveIn.Web.Tests;

// Renders pages with bUnit against a TestApp's real services (EF InMemory, fakes for email/payments/clock).
// The page sees whoever SignIn chose (anonymous by default); MudBlazor's JS calls are answered loosely.
public sealed class PageHost : IAsyncDisposable
{
    private readonly TestAuthenticationStateProvider auth = new();
    private readonly AsyncServiceScope requestScope;

    public PageHost(TestApp? app = null)
    {
        App = app ?? new TestApp();
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture ??= System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture ??= System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
        Context.JSInterop.Mode = JSRuntimeMode.Loose;
        Context.Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        // MudBlazor registers the system clock; pages must see the app's fake one.
        Context.Services.AddSingleton<TimeProvider>(App.Time);
        // Likewise its options system, which would hide the app's configured options from pages.
        Context.Services.AddSingleton(App.Get<IOptions<PlanOptions>>());
        Context.Services.AddSingleton(App.Get<IOptions<CompanyOptions>>());
        // And bUnit's placeholder authorization, so AuthorizeView uses the app's policies.
        Context.Services.AddSingleton(App.Get<IAuthorizationService>());
        Context.Services.AddSingleton(App.Get<IAuthorizationPolicyProvider>());
        Context.Services.AddSingleton<AuthenticationStateProvider>(auth);
        Context.Services.AddSingleton<AntiforgeryStateProvider, NoAntiforgery>();
        Context.Services.AddCascadingAuthenticationState();
        Context.Services.AddFallbackServiceProvider(App.Services);
        // A request gets its own DI scope, as in ASP.NET Core (the authentication handlers are per request).
        requestScope = App.Services.CreateAsyncScope();
        Request.RequestServices = requestScope.ServiceProvider;
        (Request.Request.Method, Request.Request.Scheme, Request.Request.Host) = ("GET", "https", new HostString("drive-in.test"));
        Request.User = auth.User;
        Context.Services.AddCascadingValue(_ => Request);
        Context.Services.AddScoped<IdentityRedirectManager>();
        Context.Services.AddScoped(_ =>
        {
            var signIn = Request.RequestServices.GetRequiredService<SignInManager<ApplicationUser>>();
            signIn.Context = Request;
            return signIn;
        });
        Context.Services.GetRequiredService<NavigationManager>().NavigateTo(TestApp.BaseUri);
    }

    public TestApp App { get; }
    public BunitContext Context { get; } = new();
    public NavigationManager Nav => Context.Services.GetRequiredService<NavigationManager>();

    // The request a statically rendered page (Identity's account pages, Invite) sees as its cascading HttpContext.
    // Its sign-ins and sign-outs go through the app's real cookie handlers, so they show up as Set-Cookie headers.
    public HttpContext Request { get; } = new DefaultHttpContext();

    // The request method a static page sees: GET to show it, POST when its form is submitted. Call before Render.
    public PageHost UseRequest(string method = "GET")
    {
        Request.Request.Method = method;
        return this;
    }

    // Signs in to a cookie scheme on an earlier request and sends its cookie with this one, as a browser would: e.g.
    // the external scheme after Google's callback, or the two-factor scheme after a password.
    public async Task<PageHost> CarryCookieAsync(string scheme, ClaimsPrincipal principal, AuthenticationProperties? properties = null)
    {
        await using var scope = App.Services.CreateAsyncScope();
        var earlier = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        (earlier.Request.Scheme, earlier.Request.Host) = ("https", new HostString("drive-in.test"));
        await earlier.SignInAsync(scheme, principal, properties);
        var cookies = earlier.Response.Headers.SetCookie.Select(c => c!.Split(';')[0]);
        Request.Request.Headers.Cookie = string.Join("; ", Request.Request.Headers.Cookie.Concat(cookies));
        return this;
    }

    // The cookies the page set on the response, by name.
    public IReadOnlyList<string> ResponseCookies =>
        Request.Response.Headers.SetCookie.Select(c => c!.Split('=')[0]).ToList();

    public PageHost SignIn(ClaimsPrincipal user)
    {
        auth.SetUser(user);
        Request.User = user;
        return this;
    }

    public PageHost SignIn(ApplicationUser user, bool admin = false) => SignIn(Principals.For(user, admin));

    // Where open selects and menus render their items (see Select).
    public IRenderedComponent<MudPopoverProvider>? Popovers { get; private set; }

    // Renders the page alongside a MudPopoverProvider so selects and menus can open.
    public IRenderedComponent<TPage> Render<TPage>(Action<ComponentParameterCollectionBuilder<TPage>>? parameters = null)
        where TPage : IComponent
    {
        Popovers ??= Context.Render<MudPopoverProvider>();
        return Context.Render(parameters);
    }

    // Opens the MudSelect with this label (or aria-label) on the page and picks the item whose text starts with itemText.
    public void Select<T>(IRenderedComponent<T> page, string label, string itemText, int index = 0) where T : IComponent
    {
        var select = page.FindAll(".mud-select").Where(s =>
            s.QuerySelector("label")?.TextContent.Trim() == label || s.QuerySelector($"[aria-label='{label}']") is not null).ElementAt(index);
        select.QuerySelector(".mud-input-control")!.MouseDown();
        var popovers = Popovers ?? throw new InvalidOperationException("Render the page first.");
        popovers.WaitForAssertion(() => Assert.NotEmpty(popovers.FindAll(".mud-popover-open .mud-list-item")));
        popovers.FindAll(".mud-popover-open .mud-list-item").First(i => i.TextContent.Trim().StartsWith(itemText, StringComparison.Ordinal)).Click();
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        await requestScope.DisposeAsync();
        await App.DisposeAsync();
    }

    // Forms render without a token, as there's no request to tie one to.
    private sealed class NoAntiforgery : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => null;
    }

    private sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
    {
        public ClaimsPrincipal User { get; private set; } = Principals.Anonymous;

        // Components already rendered see the change too.
        public void SetUser(ClaimsPrincipal user)
        {
            User = user;
            NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        }

        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(User));
    }
}

public static class RenderedExtensions
{
    // Waits for this text to appear on the page, e.g. once its async load or an action finishes.
    public static void WaitForText<T>(this IRenderedComponent<T> page, string text, TimeSpan? timeout = null) where T : IComponent =>
        page.WaitForAssertion(() => Assert.Contains(text, page.Text()), timeout ?? TimeSpan.FromSeconds(5));

    // The page's visible text, whitespace collapsed to single spaces. Other tags count as a space, inline formatting doesn't.
    public static string Text<T>(this IRenderedComponent<T> page) where T : IComponent
    {
        var text = Regex.Replace(page.Markup, @"</?(strong|em|b|i|code|a)\b[^>]*>", "");
        return Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", " ")), @"\s+", " ");
    }

    // Clicks the first (or last) button whose text (or an icon button's aria-label) is exactly this.
    public static void ClickButton<T>(this IRenderedComponent<T> page, string text, bool last = false) where T : IComponent
    {
        var buttons = page.FindAll("button").Where(b => b.TextContent.Trim() == text || b.GetAttribute("aria-label") == text).ToList();
        if (buttons.Count == 0)
            throw new InvalidOperationException($"No button '{text}'.");
        (last ? buttons[^1] : buttons[0]).Click();
    }

    // The inputs and textareas of the MudBlazor fields with this label (or aria-label / placeholder), in page order.
    public static List<IElement> Fields<T>(this IRenderedComponent<T> page, string label) where T : IComponent =>
        page.FindAll(".mud-input-control")
            .Select(control => (control, input: control.QuerySelector("input:not([type=checkbox]), textarea")))
            .Where(x => x.input is not null && (x.control.QuerySelector("label")?.TextContent.Trim() == label
                || x.input.GetAttribute("aria-label") == label || x.input.GetAttribute("placeholder") == label))
            .Select(x => x.input!)
            .ToList();

    public static IElement Field<T>(this IRenderedComponent<T> page, string label, int index = 0) where T : IComponent =>
        page.Fields(label).ElementAtOrDefault(index) ?? throw new InvalidOperationException($"No field labelled '{label}' at {index}.");

    // Sets a text or numeric field and fires its change, as MudBlazor fields bind on change.
    // Immediate fields bind on input instead. Index picks among fields sharing a label.
    public static void SetField<T>(this IRenderedComponent<T> page, string label, string value, int index = 0) where T : IComponent
    {
        var field = page.Field(label, index);
        try
        {
            field.Change(value);
        }
        catch (MissingEventHandlerException)
        {
            field.Input(value);
        }
    }

    // Ticks or unticks the MudCheckBox / MudSwitch whose label contains this text.
    public static void Check<T>(this IRenderedComponent<T> page, string label, bool value = true) where T : IComponent =>
        page.FindAll("label").First(l => l.TextContent.Contains(label, StringComparison.Ordinal)
            && l.QuerySelector("input[type=checkbox]") is not null).QuerySelector("input[type=checkbox]")!.Change(value);

    // Picks the MudRadio whose label contains this text.
    public static void ChooseRadio<T>(this IRenderedComponent<T> page, string label) where T : IComponent =>
        page.FindAll(".mud-radio").First(r => r.TextContent.Contains(label, StringComparison.Ordinal)).QuerySelector("input")!.Click();
}
