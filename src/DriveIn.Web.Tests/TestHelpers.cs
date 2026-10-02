using System.Security.Claims;
using System.Text.RegularExpressions;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DriveIn.Web.Tests;

public sealed class FakeEmailSender : IAppEmailSender
{
    public List<(string To, string Subject, string Body)> Sent { get; } = [];

    public List<IReadOnlyList<InlineImage>?> Images { get; } = [];

    // When set, SendAsync throws this instead of sending (e.g. SES rejecting an unverified recipient).
    public Exception? FailWith { get; set; }

    public Task SendAsync(string to, string subject, string htmlBody, IReadOnlyList<InlineImage>? images = null,
        CancellationToken ct = default)
    {
        // A faulted task, not a synchronous throw, like a real async sender.
        if (FailWith is not null)
            return Task.FromException(FailWith);
        Sent.Add((to, subject, htmlBody));
        Images.Add(images);
        return Task.CompletedTask;
    }

    // Pulls the invite token out of the most recent invitation email.
    public string LastInviteToken()
    {
        var body = Sent.Last(m => m.Subject.Contains("invited")).Body;
        return Regex.Match(body, @"/invite/([A-Za-z0-9_\-%]+)").Groups[1].Value;
    }
}

// A real DI container (Identity + EF InMemory) per test, mirroring Program.cs registrations.
public sealed class TestApp : IAsyncDisposable
{
    public const string BaseUri = "https://drive-in.test/";

    public ServiceProvider Services { get; }
    public FakeEmailSender Email { get; } = new();
    public FakePaymentProcessor Payments { get; } = new();
    public FakePayoutAccounts Payouts { get; } = new();
    public SpotEvents Events { get; } = new();
    public NotificationEvents NotificationEvents { get; } = new();
    public MessageEvents MessageEvents { get; } = new();
    public FakeGeocoder Geocoder { get; } = new();
    public FakeWeatherForecaster Weather { get; } = new();
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));

    public TestApp(string? adminEmail = null)
    {
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton<DriveInMetrics>();
        services.AddDataProtection().UseEphemeralDataProtectionProvider(); // per app, not the machine key ring
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Seed:AdminEmail"] = adminEmail })
            .Build());
        services.AddDbContextFactory<ApplicationDbContext>(o => o
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)), ServiceLifetime.Scoped);
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.SignIn.RequireConfirmedAccount = true;
                o.User.RequireUniqueEmail = true;
                o.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddSignInManager()
            .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>()
            .AddDefaultTokenProviders();
        services.AddAuthentication(o =>
            {
                o.DefaultScheme = IdentityConstants.ApplicationScheme;
                o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies();
        services.AddAuthorizationCore(o => o.AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin)));
        services.AddScoped<TheaterAccess>();
        services.AddScoped<IAuthorizationHandler, TheaterAuthorizationHandler>();
        services.AddSingleton<IAppEmailSender>(Email);
        services.AddSingleton<IEmailSender<ApplicationUser>, IdentityEmailSender>();
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton<IGeocoder>(Geocoder);
        services.AddSingleton<IWeatherForecaster>(Weather);
        services.AddScoped<WeatherService>();
        services.AddScoped<TheaterService>();
        services.AddScoped<ScreenService>();
        services.AddScoped<ScheduleService>();
        services.AddScoped<PricingService>();
        services.AddScoped<InvitationService>();
        services.AddScoped<EmployeeService>();
        services.AddScoped<UserAdminService>();
        services.AddScoped<RoleService>();
        services.AddSingleton<IPaymentProcessor>(Payments);
        services.AddSingleton<DummyPaymentProcessor>();
        services.AddSingleton<IPayoutAccounts>(Payouts);
        services.AddScoped<PayoutService>();
        services.Configure<PlanOptions>(o => o.PricePerScreenPerMonth = 49m);
        services.AddScoped<BillingService>();
        services.AddScoped<BillingReportService>();
        services.AddScoped<OnboardingService>();
        services.AddSingleton(Events);
        services.AddScoped<TicketSalesService>();
        services.AddScoped<ReportService>();
        services.AddSingleton(NotificationEvents);
        services.AddSingleton(MessageEvents);
        services.AddScoped<NotificationService>();
        services.AddScoped<MessagingService>();
        services.AddSingleton<HtmlContent>();
        services.AddScoped<ContentService>();
        Services = services.BuildServiceProvider();
        DbSeeder.SeedAsync(Services).GetAwaiter().GetResult();
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public ApplicationDbContext Db() => Get<IDbContextFactory<ApplicationDbContext>>().CreateDbContext();

    public async Task<ApplicationUser> CreateUserAsync(string email, int? employeeTheaterId = null, bool admin = false)
    {
        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, EmployeeTheaterId = employeeTheaterId };
        var result = await users.CreateAsync(user, "Password123!");
        Assert.True(result.Succeeded, string.Join(" ", result.Errors.Select(e => e.Description)));
        if (admin)
            await users.AddToRoleAsync(user, Roles.Admin);
        return user;
    }

    public async Task<Theater> CreateTheaterAsync(string name, string? ownerId = null)
    {
        await using var db = Db();
        var theater = new Theater { Name = name, Slug = name.ToLowerInvariant().Replace(' ', '-'), OwnerId = ownerId };
        db.Theaters.Add(theater);
        await db.SaveChangesAsync();
        return theater;
    }

    // Creates a role at the employee's theater with these permissions and assigns it to them.
    public async Task<TheaterRole> GrantAsync(ApplicationUser employee, params string[] permissions)
    {
        await using var db = Db();
        var role = new TheaterRole
        {
            TheaterId = employee.EmployeeTheaterId ?? throw new InvalidOperationException("Not an employee."),
            Name = "role-" + Guid.NewGuid().ToString("N")[..8],
            Permissions = permissions.Select(p => new TheaterRolePermission { Permission = p }).ToList(),
        };
        db.TheaterRoles.Add(role);
        await db.SaveChangesAsync();
        db.EmployeeRoles.Add(new EmployeeRole { UserId = employee.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return role;
    }

    public async Task<IReadOnlySet<string>> PermissionsAsync(ApplicationUser user, Theater theater, bool admin = false)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TheaterAccess>()
            .GetPermissionsAsync(Principals.For(user, admin), theater);
    }

    public async ValueTask DisposeAsync() => await Services.DisposeAsync();
}

public static class Principals
{
    public static ClaimsPrincipal For(ApplicationUser user, bool admin = false) =>
        Create(user.Id, admin, user.EmployeeTheaterId);

    public static ClaimsPrincipal Create(string userId, bool admin = false, int? employeeTheaterId = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        if (admin)
            claims.Add(new Claim(ClaimTypes.Role, Roles.Admin));
        if (employeeTheaterId is int id)
            claims.Add(new Claim(AppClaims.EmployeeTheater, id.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    public static ClaimsPrincipal Anonymous => new(new ClaimsIdentity());
}

// Approves every charge (like the dummy processor) unless told to decline; records what was charged.
public sealed class FakePaymentProcessor : IPaymentProcessor
{
    public bool IsAvailable { get; set; } = true;
    public PaymentClient Client { get; set; } = PaymentClient.Test;
    // Like Stripe (Connect) when set: a live theater needs an enabled payout account to sell.
    public bool RequiresPayoutAccount { get; set; }
    public string? DeclineWith { get; set; }
    // When set, the charge fails with this before reaching the processor (it has no record of it), as opposed to declining.
    public Exception? FailWith { get; set; }
    // When set, the charge goes through but the caller gets this instead of the answer: a timeout after the card was
    // charged, or the server dying mid-charge.
    public Exception? LoseAnswerWith { get; set; }
    public List<PaymentRequest> Charges { get; } = [];
    // What the processor knows, by idempotency key; tests may set an outcome (or Pending) directly.
    public Dictionary<string, PaymentStatus> Outcomes { get; } = [];
    public List<PaymentLookup> Lookups { get; } = [];

    public Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct = default)
    {
        // Like a real processor, a repeated key gets the first answer and isn't charged again.
        if (Outcomes.TryGetValue(request.IdempotencyKey, out var known) && known.Result is not null)
            return Task.FromResult(known.Result);
        Charges.Add(request);
        if (FailWith is not null)
            return Task.FromException<PaymentResult>(FailWith);
        // Reports the brand and last four of a test card token, as a real processor reports the card it charged.
        var card = TestCardTokens.Parse(request.PaymentMethodId);
        var result = DeclineWith is null
            ? new PaymentResult(true, $"FAKE-{Charges.Count}", CardBrand: card?.Brand, CardLast4: card?.Last4)
            : PaymentResult.Declined(DeclineWith);
        Outcomes[request.IdempotencyKey] = new PaymentStatus(result.Approved ? PaymentState.Succeeded : PaymentState.Failed, result);
        return LoseAnswerWith is null ? Task.FromResult(result) : Task.FromException<PaymentResult>(LoseAnswerWith);
    }

    public Task<PaymentStatus> GetStatusAsync(PaymentLookup lookup, CancellationToken ct = default)
    {
        Lookups.Add(lookup);
        return Task.FromResult(Outcomes.GetValueOrDefault(lookup.IdempotencyKey) ?? PaymentStatus.NotFound);
    }
}

// Payout accounts that stay Pending until a test marks them Enabled (as if the owner finished onboarding).
public sealed class FakePayoutAccounts : IPayoutAccounts
{
    public bool IsAvailable { get; set; } = true;
    public Dictionary<string, PayoutStatus> Accounts { get; } = [];
    public List<(string Account, string ReturnUrl, string RefreshUrl)> Links { get; } = [];

    public Task<string> CreateAccountAsync(Theater theater, string? email, CancellationToken ct = default)
    {
        var id = $"acct_fake{Accounts.Count + 1}";
        Accounts[id] = PayoutStatus.Pending;
        return Task.FromResult(id);
    }

    public Task<string> CreateOnboardingLinkAsync(string accountId, string returnUrl, string refreshUrl, CancellationToken ct = default)
    {
        Links.Add((accountId, returnUrl, refreshUrl));
        return Task.FromResult($"https://connect.example.test/setup/{accountId}");
    }

    public Task<PayoutStatus> GetStatusAsync(string accountId, CancellationToken ct = default) =>
        Task.FromResult(Accounts.GetValueOrDefault(accountId));
}

// Knows the places it's told about (by exact query); everything else isn't found. Records every lookup.
public sealed class FakeGeocoder : IGeocoder
{
    public Dictionary<string, GeoPoint> Places { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Queries { get; } = [];

    public Task<GeoPoint?> GeocodeAsync(string query, CancellationToken ct = default)
    {
        Queries.Add(query);
        return Task.FromResult(Places.GetValueOrDefault(query));
    }
}

// Returns Forecast (null by default) for any place, and records where it was asked for.
public sealed class FakeWeatherForecaster : IWeatherForecaster
{
    public HourlyForecast? Forecast { get; set; }
    public List<GeoPoint> Requests { get; } = [];

    public Task<HourlyForecast?> GetHourlyAsync(GeoPoint at, CancellationToken ct = default)
    {
        Requests.Add(at);
        return Task.FromResult(Forecast);
    }
}
