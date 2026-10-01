using System.Security.Claims;
using System.Text.RegularExpressions;
using DriveIn.Web.Authorization;
using DriveIn.Web.Data;
using DriveIn.Web.Services;
using Microsoft.AspNetCore.Authorization;
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
    public SpotEvents Events { get; } = new();
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
        services.AddDataProtection();
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
            .AddClaimsPrincipalFactory<AppClaimsPrincipalFactory>()
            .AddDefaultTokenProviders();
        services.AddAuthorizationCore();
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
        services.Configure<PlanOptions>(o => o.PricePerScreenPerMonth = 49m);
        services.AddScoped<BillingService>();
        services.AddScoped<BillingReportService>();
        services.AddScoped<OnboardingService>();
        services.AddSingleton(Events);
        services.AddScoped<TicketSalesService>();
        services.AddScoped<ReportService>();
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
    public string? DeclineWith { get; set; }
    // When set, the charge fails with this (the processor erroring, as opposed to declining).
    public Exception? FailWith { get; set; }
    public List<PaymentRequest> Charges { get; } = [];

    public Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct = default)
    {
        Charges.Add(request);
        if (FailWith is not null)
            return Task.FromException<PaymentResult>(FailWith);
        return Task.FromResult(DeclineWith is null ? new PaymentResult(true, $"FAKE-{Charges.Count}") : PaymentResult.Declined(DeclineWith));
    }
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
