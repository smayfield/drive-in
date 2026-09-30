using System.Net;
using Amazon.SimpleEmailV2;
using DriveIn.Web.Authorization;
using DriveIn.Web.Components;
using DriveIn.Web.Components.Account;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using MudBlazor.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// In production the app runs behind Caddy, which terminates TLS.
// Trust its X-Forwarded-* headers so Google OAuth and emailed links use https://.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    var knownNetworks = builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks")
        .GetChildren()
        .Select(c => c.Value)
        .OfType<string>()
        .ToArray();
    var knownProxies = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies")
        .GetChildren()
        .Select(c => c.Value)
        .OfType<string>()
        .ToArray();

    if (knownNetworks.Length > 0 || knownProxies.Length > 0)
    {
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();

        foreach (var network in knownNetworks)
            o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        foreach (var proxy in knownProxies)
            o.KnownProxies.Add(IPAddress.Parse(proxy));
    }
});

// Persist Data Protection keys so auth cookies and emailed tokens survive container restarts/deploys.
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrEmpty(keysPath))
{
    builder.Services.AddDataProtection()
        .SetApplicationName("DriveIn")
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

builder.Services.AddMudServices();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

var authentication = builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    });

// Google is optional so local accounts work in development without OAuth credentials.
var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
if (!string.IsNullOrEmpty(googleClientId))
{
    authentication.AddGoogle(o =>
    {
        o.ClientId = googleClientId;
        o.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"]
            ?? throw new InvalidOperationException("Authentication:Google:ClientSecret is not configured.");
        // Used to check that an invite is accepted by the Google account it was sent to.
        o.ClaimActions.MapJsonKey("email_verified", "email_verified");
    });
}
authentication.AddIdentityCookies();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
// The factory serves interactive components (one short-lived context per operation);
// it also registers a scoped ApplicationDbContext for the Identity stores.
builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention(), ServiceLifetime.Scoped);
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = true;
        options.User.RequireUniqueEmail = true;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>()
    .AddDefaultTokenProviders();

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin));
builder.Services.AddScoped<TheaterAccess>();
builder.Services.AddScoped<IAuthorizationHandler, TheaterAuthorizationHandler>();

builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.Section));
if (builder.Configuration[$"{EmailOptions.Section}:Provider"] == "Ses")
{
    // Region and credentials come from the environment (AWS_REGION + the EC2 instance role).
    builder.Services.AddSingleton<IAmazonSimpleEmailServiceV2, AmazonSimpleEmailServiceV2Client>();
    builder.Services.AddSingleton<IAppEmailSender, SesEmailSender>();
}
else
{
    builder.Services.AddSingleton<IAppEmailSender, LoggingEmailSender>();
}
builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityEmailSender>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<TheaterService>();
builder.Services.AddScoped<ScreenService>();
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddScoped<PricingService>();
builder.Services.AddScoped<InvitationService>();
builder.Services.AddScoped<EmployeeService>();
builder.Services.AddScoped<UserAdminService>();
builder.Services.AddScoped<RoleService>();
builder.Services.Configure<PlanOptions>(builder.Configuration.GetSection(PlanOptions.Section));
builder.Services.Configure<CompanyOptions>(builder.Configuration.GetSection(CompanyOptions.Section));
builder.Services.AddScoped<OnboardingService>();
builder.Services.Configure<BillingOptions>(builder.Configuration.GetSection(BillingOptions.Section));
builder.Services.AddScoped<BillingService>();
builder.Services.AddScoped<BillingReportService>();

// Online ticket sales. Card payments are off unless a processor is configured; "Dummy" (development only) approves
// everything without taking money.
builder.Services.Configure<PaymentOptions>(builder.Configuration.GetSection(PaymentOptions.Section));
builder.Services.AddSingleton<DummyPaymentProcessor>(); // also used for demo theaters' test sales
if (builder.Configuration[$"{PaymentOptions.Section}:Provider"] == "Dummy")
    builder.Services.AddSingleton<IPaymentProcessor>(sp => sp.GetRequiredService<DummyPaymentProcessor>());
else
    builder.Services.AddSingleton<IPaymentProcessor, UnavailablePaymentProcessor>();
builder.Services.AddSingleton<SpotEvents>();
builder.Services.AddScoped<TicketSalesService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddHostedService<HoldExpiryService>();
builder.Services.AddHostedService<BillingJobService>();

var app = builder.Build();

app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

// Explicit so they run after UseForwardedHeaders. Left implicit, WebApplication inserts them at the
// start of the pipeline, where the Google callback and login redirects would see http:// behind Caddy.
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

// A theater's logo. Signed-in users only, like the theater pages, and only for theaters they may browse.
app.MapGet("/theaters/{slug}/logo", async (string slug, HttpContext http, TheaterService theaters) =>
{
    var logo = await theaters.GetLogoAsync(http.User, slug);
    if (logo is null)
        return Results.NotFound();
    http.Response.Headers.XContentTypeOptions = "nosniff";
    // The page URL carries a version (?v=) that changes with each upload, so a browser can keep it for a day.
    http.Response.Headers.CacheControl = "private, max-age=86400";
    return Results.File(logo.Data, logo.ContentType);
}).RequireAuthorization();

// A film's poster, visible to whoever may browse the film's theater.
app.MapGet("/films/{filmId:int}/poster", async (int filmId, HttpContext http, ScheduleService schedule) =>
{
    var poster = await schedule.GetPosterAsync(http.User, filmId);
    if (poster is null)
        return Results.NotFound();
    http.Response.Headers.XContentTypeOptions = "nosniff";
    http.Response.Headers.CacheControl = "private, max-age=86400";
    return Results.File(poster.Data, poster.ContentType);
}).RequireAuthorization();

// A report as CSV, for whoever may view the theater's reports (the service checks). ?from=&to= are yyyy-MM-dd dates.
app.MapGet("/manage/{theaterId:int}/reports/{kind}.csv", async (int theaterId, string kind, string? from, string? to,
    HttpContext http, ReportService reports, TheaterService theaters) =>
{
    if (!ReportCsv.Kinds.Contains(kind))
        return Results.NotFound();
    if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", out var fromDate) || !DateOnly.TryParseExact(to, "yyyy-MM-dd", out var toDate))
        return Results.BadRequest("Give from and to dates as yyyy-MM-dd.");
    try
    {
        string csv;
        if (kind == "giftcards")
            csv = ReportCsv.GiftCards(await reports.GetGiftCardReportAsync(http.User, theaterId, fromDate, toDate));
        else
        {
            var report = await reports.GetSalesReportAsync(http.User, theaterId, fromDate, toDate);
            csv = kind switch { "days" => ReportCsv.Days(report), "films" => ReportCsv.Films(report), _ => ReportCsv.Showings(report) };
        }
        var slug = (await theaters.GetForManageAsync(http.User, theaterId)).Slug;
        http.Response.Headers.CacheControl = "no-store";
        return Results.File(ReportCsv.ToBytes(csv), "text/csv; charset=utf-8", $"{slug}-{kind}-{from}-{to}.csv");
    }
    catch (AccessDeniedException)
    {
        return Results.Forbid();
    }
    catch (NotFoundException)
    {
        return Results.NotFound();
    }
    catch (AppValidationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
}).RequireAuthorization();

// Admin billing reports as CSV. ?from=&to= are months as yyyy-MM (both included).
app.MapGet("/admin/billing/{kind}.csv", async (string kind, string? from, string? to, HttpContext http, BillingReportService reports) =>
{
    if (!ReportCsv.BillingKinds.Contains(kind))
        return Results.NotFound();
    if (!DateOnly.TryParseExact(from + "-01", "yyyy-MM-dd", out var fromMonth) || !DateOnly.TryParseExact(to + "-01", "yyyy-MM-dd", out var toMonth))
        return Results.BadRequest("Give from and to months as yyyy-MM.");
    try
    {
        var report = await reports.GetReportAsync(http.User, fromMonth, toMonth);
        var csv = kind switch
        {
            "payments" => ReportCsv.BillingPayments(report),
            "aging" => ReportCsv.BillingAging(report),
            _ => ReportCsv.BillingInvoices(report),
        };
        http.Response.Headers.CacheControl = "no-store";
        return Results.File(ReportCsv.ToBytes(csv), "text/csv; charset=utf-8", $"billing-{kind}-{from}-{to}.csv");
    }
    catch (AccessDeniedException)
    {
        return Results.Forbid();
    }
    catch (AppValidationException ex)
    {
        return Results.BadRequest(ex.Message);
    }
}).RequireAuthorization(Policies.Admin);

await DbSeeder.SeedAsync(app.Services);

app.Run();
